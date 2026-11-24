// settings.cs's logic: check, and with --fix apply, the Baseline's GitHub settings on a repo.
//
//     github-settings.sh <owner/repo>...            report; exit 1 on any drift
//     github-settings.sh --fix <owner/repo>...      report, apply, report again
//     github-settings.sh --all [--fix]              every repo in github/repos.txt
//     github-settings.sh --suspend <owner/repo>     emergency: main-rules off
//     add --remove-legacy to --fix to delete the legacy items it lists
//     add --json for machine-readable output
//
// apply.sh copies the Baseline's files; this applies the settings files can't carry: merge settings, security features, Actions
// permissions, the main-rules ruleset and its required checks, labels, and a check that RELEASE_PR_PAT exists. The standard is the JSON
// beside this file; github/repos/<repo>.json may add required checks or labels for one repo, never remove or loosen anything. See
// references/github-settings.md.
//
// It changes only what it manages. Other rulesets, labels and classic branch protection are reported; --remove-legacy deletes only
// those legacy.json names, and classic protection only once main-rules is active. Squad is retired, so its labels (settings.json's
// removed_label_prefixes) go on every --fix. A required check is added only once a workflow on the default branch has the job that
// reports it, so requiring a check can't block every PR before the repo produces it.
//
// PR Auto-Merge waits for a review of the head for at most three review rounds, and holds on every unresolved thread past them (#146).
// A ruleset can't express the cap, so main-rules leaves thread resolution off and the Template's pr-automerge.yml enforces both; this
// checks every repo runs that cap.
//
// GitHub is reached through `gh api`. It prints what settings.py printed, byte for byte (the settings parity suite): Python's repr()
// of a setting, json.dumps() of a ruleset part, and the --json report as json.dump(indent=2) wrote it, so Py below holds Python's
// readings of a JSON value. A repo that isn't owner/repo is refused before any call, which settings.py didn't do (ADR 0017's Update for
// mpaulosky/Application_Factory#120).
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// A `gh api` call that failed, with the HTTP status its stderr named (0 when it named none).
sealed class ApiError(int status, string message) : Exception($"HTTP {status}: {message}")
{
	public int Status { get; } = status;
}

// A per-repo file that would loosen the standard: its message on stderr and exit 1, as settings.py's SystemExit was.
sealed class StandardError(string message) : Exception(message);

// One `gh api -X <method> <path>` call: its parsed answer (null for an empty one), or ApiError. A body is sent as JSON on stdin.
delegate JsonNode? Api(string method, string path, JsonNode? body = null);

// What one check found: OK, DRIFT, MANUAL, LEGACY, UNAVAILABLE or INFO, and the change that applies it, if --fix can.
sealed record Finding(string Area, string Status, string Detail, Action? Fix = null);

// The standard for one repo: settings.json, main-rules.json, labels.json and legacy.json, with its github/repos/<name>.json added.
sealed record Standard(JsonObject Settings, JsonObject Ruleset, JsonArray Labels, JsonObject Legacy)
{
	public JsonNode? Setting(string key) => Py.Get(Settings, key);
}

static class Settings
{
	public const int ActionsAppId = 15368; // GitHub Actions, the app that reports every required check
	public const string OK = "OK", Drift = "DRIFT", Manual = "MANUAL", Legacy = "LEGACY", Unavailable = "UNAVAILABLE", Info = "INFO";
	static readonly HashSet<string> FailingStatuses = [Drift, Manual, Legacy];
	const string Accept = "Accept: application/vnd.github+json";

	// gh's api: every call is `gh api -X <method> <path> -H <Accept>`, and one with a body adds `--input -` and writes the body on stdin.
	// process runs gh (Gh.Process); a test hands in its own.
	public static Api GhApi(Func<IReadOnlyList<string>, string?, GhRun>? process = null) => (method, path, body) =>
	{
		List<string> argv = ["api", "-X", method, path, "-H", Accept];
		if (body is not null)
			argv.AddRange(["--input", "-"]);
		var run = (process ?? Gh.Process)(argv, body?.ToJsonString());
		if (run.Code != 0)
		{
			var status = Regex.Match(run.Stderr, @"HTTP (\d{3})");
			var message = Py.Strip(run.Stderr) is { Length: > 0 } stderr ? stderr : Py.Strip(run.Stdout);
			throw new ApiError(status.Success ? (int)Py.ParseInt(status.Groups[1].Value) : 0, message);
		}
		return Py.Strip(run.Stdout).Length > 0 ? JsonNode.Parse(run.Stdout) : null;
	};

	// The standard for repo, read from folder: the shared files plus its additive github/repos/<name>.json.
	public static Standard Load(string folder, string repo)
	{
		JsonNode? Read(string path) => JsonNode.Parse(File.ReadAllText(path));
		var settings = Read(Path.Combine(folder, "settings.json")) as JsonObject ?? throw new InvalidOperationException("settings.json is not an object");
		var ruleset = Read(Path.Combine(folder, "main-rules.json")) as JsonObject ?? throw new InvalidOperationException("main-rules.json is not an object");
		var labels = Read(Path.Combine(folder, "labels.json")) as JsonArray ?? throw new InvalidOperationException("labels.json is not a list");
		var legacy = Read(Path.Combine(folder, "legacy.json")) as JsonObject ?? throw new InvalidOperationException("legacy.json is not an object");
		var extraPath = Path.Combine(folder, "repos", $"{repo.Split('/')[^1]}.json");
		if (File.Exists(extraPath))
		{
			var extra = Read(extraPath) as JsonObject ?? throw new InvalidOperationException($"{extraPath} is not an object");
			var unknown = extra.Select(pair => pair.Key).Where(key => key is not ("required_checks" or "labels")).Order(Py.CodePoints).ToList();
			if (unknown.Count > 0)
				throw new StandardError($"{extraPath}: only required_checks and labels may be added, not {Py.Repr(new JsonArray([.. unknown.Select(key => (JsonNode)key)]))}");
			var checks = Py.List(J.Req(settings, "required_checks")) as JsonArray ?? throw new InvalidOperationException("required_checks is not a list");
			var added = Py.Items(Py.Get(extra, "required_checks", new JsonArray())).Where(check => !checks.Any(have => Py.Eq(have, check))).ToList();
			foreach (var check in added)
				checks.Add(J.Copy(check));
			var names = labels.Select(label => J.Req(label, "name")).ToList();
			foreach (var label in Py.Items(Py.Get(extra, "labels", new JsonArray())).Where(label => !names.Any(name => Py.Eq(name, J.Req(label, "name")))).ToList())
				labels.Add(J.Copy(label));
		}
		return new(settings, ruleset, labels, legacy);
	}

	// ── Workflows on the default branch ──────────────────────────────────────────

	// Python's \s and \S: .NET's \s lacks the four separators \x1c to \x1f that str.isspace() counts.
	const string Ws = @"[\s\x1c-\x1f]", NotWs = @"[^\s\x1c-\x1f]";
	static readonly Regex Job = new($@"^  ([A-Za-z0-9_-]+):{Ws}*$", RegexOptions.CultureInvariant);
	static readonly Regex JobName = new($@"^    name:{Ws}*(.+?){Ws}*$", RegexOptions.CultureInvariant);
	static readonly Regex Uses = new($@"^{Ws}*-?{Ws}*uses:{Ws}*['""]?([^'""\s\x1c-\x1f#]+)", RegexOptions.CultureInvariant);
	static readonly Regex Pinned = new(@"^(\./|docker://.+@sha256:[0-9a-f]{64}$|[^@]+@[0-9a-f]{40}$)", RegexOptions.CultureInvariant);
	static readonly Regex JobsKey = new($@"^jobs:{Ws}*$", RegexOptions.CultureInvariant);
	static readonly Regex TopLevel = new($@"^{NotWs}", RegexOptions.CultureInvariant);

	// (check names the workflow's jobs report, unpinned `uses:` references) for one workflow file. A job reports its name:, or its id when
	// it has none. A templated name (a matrix job's) can't be required, so it's left out.
	public static (List<string> Names, List<string> Unpinned) WorkflowFacts(string text)
	{
		List<string> names = [], unpinned = [];
		var inJobs = false;
		string? job = null;
		var named = false;
		foreach (var line in Py.SplitLines(text))
		{
			if (JobsKey.IsMatch(line))
			{
				inJobs = true;
				continue;
			}
			if (inJobs && TopLevel.IsMatch(line))
				inJobs = false;
			Match match;
			if (inJobs && (match = Job.Match(line)).Success)
			{
				if (!string.IsNullOrEmpty(job) && !named)
					names.Add(job);
				(job, named) = (match.Groups[1].Value, false);
			}
			else if (inJobs && !string.IsNullOrEmpty(job) && !named && (match = JobName.Match(line)).Success)
			{
				named = true;
				var name = match.Groups[1].Value.Trim('\'', '"');
				if (!name.Contains("${{", StringComparison.Ordinal))
					names.Add(name);
			}
			if ((match = Uses.Match(line)).Success && !Pinned.IsMatch(match.Groups[1].Value))
				unpinned.Add(match.Groups[1].Value);
		}
		if (!string.IsNullOrEmpty(job) && !named)
			names.Add(job);
		return (names, unpinned);
	}

	static readonly Regex OnKey = new(@"^(?:""on""|'on'|on):(.*)$", RegexOptions.CultureInvariant);
	// A pull_request trigger without these activity types misses a PR's first head (opened) or its later ones (synchronize). The default
	// types include both.
	static readonly string[] PrHeadTypes = ["opened", "synchronize"];
	static readonly Regex PullRequestWord = new(@"\bpull_request\b(?!_)", RegexOptions.CultureInvariant);
	static readonly Regex PullRequestEntry = new($@"^  (?:pull_request|- pull_request){Ws}*:?{Ws}*(?:#.*)?$", RegexOptions.CultureInvariant);
	static readonly Regex PathsFilter = new($@"^{Ws}+paths(-ignore)?:", RegexOptions.CultureInvariant);
	static readonly Regex ListItem = new($@"^{Ws}+-{Ws}*(.+?){Ws}*(?:#.*)?$", RegexOptions.CultureInvariant);

	// The values of key (types, branches, ...) under a trigger's lines, or null when it isn't set. Reads the flow form (key: [a, b]), a
	// single value (key: a) and the block form (key: then - a lines).
	public static List<string>? TriggerList(IReadOnlyList<string> trigger, string key)
	{
		var keyLine = new Regex($@"^{Ws}+{Regex.Escape(key)}:(.*)$", RegexOptions.CultureInvariant);
		for (var position = 0; position < trigger.Count; position++)
		{
			var match = keyLine.Match(trigger[position]);
			if (!match.Success)
				continue;
			var inline = Py.Strip(match.Groups[1].Value.Split('#')[0]);
			if (inline.Length > 0)
				return [.. inline.Trim('[', ']').Split(',').Where(value => Py.Strip(value).Length > 0).Select(value => Py.Strip(value).Trim('\'', '"'))];
			List<string> values = [];
			foreach (var nested in trigger.Skip(position + 1))
			{
				if (ListItem.Match(nested) is { Success: true } item)
					values.Add(item.Groups[1].Value.Trim('\'', '"'));
				else if (Py.Strip(nested).Length > 0 && !Py.LStrip(nested).StartsWith('#'))
					break;
			}
			return values;
		}
		return null;
	}

	static readonly Regex BracketClass = new(@"\G\[([A-Za-z0-9-]+)\]", RegexOptions.CultureInvariant);

	// A workflow branch filter pattern as a regex, with GitHub's filter syntax: * is any characters but /, ** any characters, ? zero or
	// one of the character before it, + one or more of it, [a-z0-9] one listed alphanumeric, and \ escapes the next character. A
	// character is a code point, as in Python: one outside the BMP is one group, so a ? or + after it applies to all of it.
	public static string BranchRegex(string pattern)
	{
		List<string> parts = [];
		string Literal(int at) => char.IsSurrogatePair(pattern, at) ? $"(?:{Regex.Escape(pattern.Substring(at, 2))})" : Regex.Escape(pattern[at].ToString());
		int Width(int at) => char.IsSurrogatePair(pattern, at) ? 2 : 1;
		var index = 0;
		while (index < pattern.Length)
		{
			var c = pattern[index];
			if (c == '\\' && index + 1 < pattern.Length)
			{
				parts.Add(Literal(index + 1));
				index += 1 + Width(index + 1);
				continue;
			}
			if (string.CompareOrdinal(pattern, index, "**", 0, 2) == 0)
			{
				parts.Add(".*");
				index += 2;
				continue;
			}
			Match match;
			if (c == '*')
				parts.Add("[^/]*");
			else if (c is '?' or '+' && parts.Count > 0 && !(parts[^1].EndsWith('*') || parts[^1].EndsWith('?') || parts[^1].EndsWith('+')))
				parts.Add(c.ToString());
			else if (c == '[' && (match = BracketClass.Match(pattern, index)).Success)
			{
				parts.Add(match.Value);
				index += match.Length;
				continue;
			}
			else
			{
				parts.Add(Literal(index));
				index += Width(index);
				continue;
			}
			index++;
		}
		return string.Concat(parts);
	}

	// Whether a workflow branch filter pattern matches branch; a pattern GitHub wouldn't accept either matches nothing.
	public static bool BranchMatches(string pattern, string branch)
	{
		try
		{
			return Regex.IsMatch(branch, $@"\A(?:{BranchRegex(pattern)})\z", RegexOptions.CultureInvariant);
		}
		catch (ArgumentException)
		{
			return false;
		}
	}

	// Whether the trigger's branches / branches-ignore filters let it run for PRs into branch.
	public static bool FiltersInclude(IReadOnlyList<string> trigger, string branch)
	{
		if (TriggerList(trigger, "branches-ignore") is { } ignored)
			return !ignored.Any(pattern => BranchMatches(pattern, branch));
		if (TriggerList(trigger, "branches") is not { } patterns)
			return true;
		var included = false;
		foreach (var pattern in patterns) // Later patterns win, so a !pattern can exclude an earlier match.
		{
			if (pattern.StartsWith('!'))
				included = included && !BranchMatches(pattern[1..], branch);
			else if (BranchMatches(pattern, branch))
				included = true;
		}
		return included;
	}

	// Whether the workflow reports on every head of every pull request into branch. Only such a workflow's checks can be required: a check
	// that a PR's workflows never report would hold that PR forever. So it needs a pull_request trigger whose types (if set) include opened
	// and synchronize, whose branch filters include branch, and with no paths filter. That leaves out a scheduled workflow, a
	// types: [closed] one (release.yml), and one skipped by paths-ignore, like CodeQL's on docs PRs.
	public static bool RunsOnEveryPr(string text, string branch = "main")
	{
		var lines = Py.SplitLines(text);
		for (var index = 0; index < lines.Count; index++)
		{
			var match = OnKey.Match(lines[index]);
			if (!match.Success)
				continue;
			var inline = Py.Strip(match.Groups[1].Value.Split('#')[0]);
			// A flow mapping (on: {pull_request: {paths: [...]}}) could hide any filter, and its filters aren't parsed, so it doesn't count.
			if (inline.StartsWith('{'))
				return false;
			if (inline.Length > 0) // on: pull_request / on: [push, pull_request]
				return PullRequestWord.IsMatch(inline);
			List<string> block = [];
			foreach (var following in lines.Skip(index + 1))
			{
				if (Py.Strip(following).Length > 0 && !(following.StartsWith(' ') || following.StartsWith('#')))
					break;
				block.Add(following);
			}
			for (var position = 0; position < block.Count; position++)
			{
				if (!PullRequestEntry.IsMatch(block[position]))
					continue;
				List<string> trigger = [];
				foreach (var nested in block.Skip(position + 1))
				{
					if (Py.Strip(nested).Length > 0 && !nested.StartsWith("    ", StringComparison.Ordinal) && !Py.LStrip(nested).StartsWith('#'))
						break;
					trigger.Add(nested);
				}
				if (trigger.Any(PathsFilter.IsMatch))
					return false;
				if (TriggerList(trigger, "types") is { } types && !PrHeadTypes.All(types.Contains))
					return false;
				return FiltersInclude(trigger, branch);
			}
			return false;
		}
		return false;
	}

	// {path: text} for the workflows on branch; empty when the repo has none.
	public static OrderedDictionary<string, string> ReadWorkflows(Api gh, string repo, string branch)
	{
		JsonNode? listing;
		try
		{
			listing = gh("GET", $"repos/{repo}/contents/.github/workflows?ref={branch}");
		}
		catch (ApiError error) when (error.Status == 404)
		{
			return [];
		}
		OrderedDictionary<string, string> texts = [];
		foreach (var entry in Py.Items(listing))
		{
			var name = J.S(J.Req(entry, "name")) ?? throw new InvalidOperationException("a workflow's name is not a string");
			if (!name.EndsWith(".yml", StringComparison.Ordinal) && !name.EndsWith(".yaml", StringComparison.Ordinal))
				continue;
			var path = Py.Str(J.Req(entry, "path"));
			var blob = gh("GET", $"repos/{repo}/contents/{path}?ref={branch}");
			texts[path] = Encoding.UTF8.GetString(Py.B64Decode(J.S(J.Req(blob, "content")) ?? throw new InvalidOperationException("a workflow's content is not a string")));
		}
		return texts;
	}

	// The last part of a path, as pathlib's .name reads it.
	static string FileName(string path) => path.TrimEnd('/').Split('/')[^1];

	// ── The checks ───────────────────────────────────────────────────────────────

	// "key: live → wanted" for each wanted key the live value differs from, as repr() prints both; empty when every one matches.
	static OrderedDictionary<string, JsonNode?> Differing(JsonNode? live, JsonNode? want)
	{
		OrderedDictionary<string, JsonNode?> diff = [];
		foreach (var (key, value) in Py.Pairs(want))
		{
			if (!Py.Eq(Py.Get(live, key), value))
				diff[key] = value;
		}
		return diff;
	}

	static string Describe(JsonNode? live, OrderedDictionary<string, JsonNode?> diff) =>
			string.Join(", ", diff.Select(pair => $"{pair.Key}: {Py.Repr(Py.Get(live, pair.Key))} → {Py.Repr(pair.Value)}"));

	public static List<Finding> CheckRepoFlags(Api gh, string repo, JsonNode? info, Standard std)
	{
		var diff = Differing(info, std.Setting("repo"));
		if (diff.Count == 0)
			return [new("repo settings", OK, "merge, branch and forking settings")];
		var body = new JsonObject(diff.Select(pair => KeyValuePair.Create(pair.Key, J.Copy(pair.Value))));
		return [new("repo settings", Drift, Describe(info, diff), () => gh("PATCH", $"repos/{repo}", body))];
	}

	public static List<Finding> CheckSecurity(Api gh, string repo, JsonNode? info, Standard std, OrderedDictionary<string, string> workflows)
	{
		var want = std.Setting("security");
		List<Finding> findings = [];
		var analysis = Py.Get(info, "security_and_analysis");
		if (analysis is null)
			findings.Add(new("secret scanning", Unavailable, "not offered for this repo (private without GHAS)"));
		else
		{
			foreach (var key in new[] { "secret_scanning", "secret_scanning_push_protection" })
			{
				if (!J.B(Py.Get(want, key)))
					continue;
				var feature = Py.Get(analysis, key);
				var current = Py.Get(J.B(feature) ? feature : new JsonObject(), "status");
				if (J.S(current) == "enabled")
					findings.Add(new(key, OK, "enabled"));
				else
				{
					var body = new JsonObject { ["security_and_analysis"] = new JsonObject { [key] = new JsonObject { ["status"] = "enabled" } } };
					findings.Add(new(key, Drift, $"{Py.Str(current)} → enabled", () => gh("PATCH", $"repos/{repo}", body)));
				}
			}
		}
		if (J.B(Py.Get(want, "vulnerability_alerts")))
		{
			try
			{
				gh("GET", $"repos/{repo}/vulnerability-alerts");
				findings.Add(new("Dependabot alerts", OK, "enabled"));
			}
			catch (ApiError error) when (error.Status == 404)
			{
				findings.Add(new("Dependabot alerts", Drift, "disabled → enabled", () => gh("PUT", $"repos/{repo}/vulnerability-alerts")));
			}
		}
		if (J.B(Py.Get(want, "automated_security_fixes")))
		{
			JsonNode? fixes;
			try
			{
				fixes = gh("GET", $"repos/{repo}/automated-security-fixes");
				fixes = J.B(fixes) ? fixes : new JsonObject();
			}
			catch (ApiError error) when (error.Status == 404)
			{
				fixes = new JsonObject();
			}
			if (J.B(Py.Get(fixes, "enabled")))
				findings.Add(new("Dependabot security updates", OK, "enabled"));
			else
				findings.Add(new("Dependabot security updates", Drift, "disabled → enabled", () => gh("PUT", $"repos/{repo}/automated-security-fixes")));
		}
		// GitHub rejects the advanced CodeQL workflow's results while default setup is on.
		JsonNode? setup;
		try
		{
			setup = gh("GET", $"repos/{repo}/code-scanning/default-setup");
		}
		catch (ApiError error) when (error.Status is 403 or 404)
		{
			findings.Add(new("CodeQL default setup", Unavailable, "code scanning isn't offered for this repo"));
			return findings;
		}
		var advanced = workflows.Keys.Any(path => FileName(path).StartsWith("codeql", StringComparison.Ordinal));
		if (J.S(Py.Get(setup, "state")) == "configured" && advanced)
			findings.Add(new("CodeQL default setup", Drift, "configured → not-configured (codeql-analysis.yml runs CodeQL)",
					() => gh("PATCH", $"repos/{repo}/code-scanning/default-setup", new JsonObject { ["state"] = "not-configured" })));
		else
			findings.Add(new("CodeQL default setup", OK, Py.Str(Py.Get(setup, "state", "unknown"))));
		return findings;
	}

	public static List<Finding> CheckActions(Api gh, string repo, Standard std, OrderedDictionary<string, string> workflows)
	{
		List<Finding> findings = [];
		var want = std.Setting("actions_workflow");
		var current = gh("GET", $"repos/{repo}/actions/permissions/workflow");
		var diff = Differing(current, want);
		if (diff.Count > 0)
			findings.Add(new("Actions token", Drift, Describe(current, diff), () => gh("PUT", $"repos/{repo}/actions/permissions/workflow", J.Copy(want))));
		else
			findings.Add(new("Actions token", OK, "read by default; may create PRs"));
		if (J.B(std.Setting("sha_pinning_required")))
		{
			var permissions = gh("GET", $"repos/{repo}/actions/permissions");
			var unpinned = workflows.Values.SelectMany(text => WorkflowFacts(text).Unpinned).Distinct().Order(Py.CodePoints).ToList();
			if (unpinned.Count > 0)
			{
				// Whether or not it's required yet: once it is, GitHub refuses these.
				var detail = J.B(Py.Get(permissions, "sha_pinning_required"))
						? "required, so GitHub refuses to run these until they're pinned to a commit SHA: "
						: "not required yet; pin these to a commit SHA first: ";
				findings.Add(new("pinned actions", Manual, detail + string.Join(", ", unpinned)));
			}
			else if (J.B(Py.Get(permissions, "sha_pinning_required")))
				findings.Add(new("pinned actions", OK, "required"));
			else
			{
				var body = new JsonObject { ["enabled"] = true, ["allowed_actions"] = J.Copy(Py.Get(permissions, "allowed_actions", "all")), ["sha_pinning_required"] = true };
				findings.Add(new("pinned actions", Drift, "not required → required", () => gh("PUT", $"repos/{repo}/actions/permissions", body)));
			}
		}
		return findings;
	}

	// main-rules as it should be; (ruleset, standard checks the repo can't report yet, extra checks kept). A standard check is required
	// only once a workflow reports it. A check the live ruleset requires outside the standard stays required while a workflow still
	// reports it: before a Standardize, that's the repo's only test gate (IssueManager's All Tests Passed), and dropping it first would
	// leave none.
	public static (JsonObject Ruleset, List<string> Missing, List<string> Kept) WantedRuleset(Standard std, IReadOnlySet<string> available,
			IReadOnlyList<JsonNode?>? liveRequired = null)
	{
		var ruleset = (JsonObject)std.Ruleset.DeepClone();
		var required = Py.Items(std.Setting("required_checks")).ToList();
		var present = required.Where(name => J.S(name) is { } s && available.Contains(s)).ToList();
		var missing = required.Where(name => !(J.S(name) is { } s && available.Contains(s))).Select(Py.Text).ToList();
		var kept = (liveRequired ?? []).Where(name => !required.Any(have => Py.Eq(have, name)) && J.S(name) is { } s && available.Contains(s)).ToList();
		present.AddRange(kept);
		var rules = J.Req(ruleset, "rules") as JsonArray ?? throw new InvalidOperationException("main-rules.json's rules is not a list");
		foreach (var rule in rules)
		{
			if (J.S(J.Req(rule, "type")) == "required_status_checks")
				J.Req(rule, "parameters")!.AsObject()["required_status_checks"] =
						new JsonArray([.. present.Select(name => (JsonNode)new JsonObject { ["context"] = J.Copy(name), ["integration_id"] = ActionsAppId })]);
		}
		// GitHub refuses the rule with no checks, as for a repo with no workflows yet.
		if (present.Count == 0)
			ruleset["rules"] = new JsonArray([.. rules.Where(rule => J.S(J.Req(rule, "type")) != "required_status_checks").Select(J.Copy)]);
		return (ruleset, missing, [.. kept.Select(Py.Text)]);
	}

	// One rule type to its parameters (or {}), the last of a type winning, in the order each type first appears, as a dict comprehension builds it.
	static OrderedDictionary<string, JsonNode?> RulesByType(JsonNode? rules)
	{
		OrderedDictionary<string, JsonNode?> byType = [];
		foreach (var rule in Py.Items(rules))
		{
			var parameters = Py.Get(rule, "parameters");
			byType[Py.Str(J.Req(rule, "type"))] = J.B(parameters) ? parameters : new JsonObject();
		}
		return byType;
	}

	// [context, integration_id] for each check, sorted as Python sorts the pairs.
	static JsonArray CheckPairs(JsonNode? checks) =>
			Py.Sorted(Py.Items(J.B(checks) ? checks : new JsonArray()).Select(check => (JsonNode?)new JsonArray(J.Copy(J.Req(check, "context")), J.Copy(Py.Get(check, "integration_id")))));

	// Human-readable differences between a live ruleset and the wanted one; empty when it matches.
	public static List<string> RulesetDifferences(JsonNode? live, JsonObject want)
	{
		List<string> diffs = [];
		foreach (var key in new[] { "target", "enforcement", "conditions", "bypass_actors" })
		{
			var liveValue = J.Copy(Py.Get(live, key));
			if (key == "conditions")
			{
				var refName = Py.Get(J.B(liveValue) ? liveValue : new JsonObject(), "ref_name", new JsonObject());
				liveValue = new JsonObject
				{
					["ref_name"] = new JsonObject
					{
						["include"] = Py.Sorted(Py.Items(Py.Get(refName, "include", new JsonArray())).Select(J.Copy)),
						["exclude"] = Py.Sorted(Py.Items(Py.Get(refName, "exclude", new JsonArray())).Select(J.Copy)),
					},
				};
			}
			if (!Py.Eq(liveValue, J.Req(want, key)))
				diffs.Add($"{key}: {Py.Dumps(liveValue)} → {Py.Dumps(J.Req(want, key))}");
		}
		var liveRules = RulesByType(Py.Get(live, "rules", new JsonArray()));
		var wantRules = RulesByType(J.Req(want, "rules"));
		foreach (var extra in liveRules.Keys.Where(type => !wantRules.ContainsKey(type)).Order(Py.CodePoints))
			diffs.Add($"rule {extra}: remove");
		foreach (var (ruleType, parameters) in wantRules)
		{
			if (!liveRules.TryGetValue(ruleType, out var liveParameters))
			{
				diffs.Add($"rule {ruleType}: add");
				continue;
			}
			foreach (var (key, wanted) in Py.Pairs(parameters))
			{
				var current = Py.Get(liveParameters, key);
				var value = wanted;
				if (key == "required_status_checks")
				{
					// The app matters too: a check any app can report isn't a gate.
					current = CheckPairs(current);
					value = CheckPairs(value);
				}
				else if (key == "allowed_merge_methods")
				{
					current = Py.Sorted(Py.Items(J.B(current) ? current : new JsonArray()).Select(J.Copy));
					value = Py.Sorted(Py.Items(value).Select(J.Copy));
				}
				if (!Py.Eq(current, value))
					diffs.Add($"{ruleType}.{key}: {Py.Dumps(current)} → {Py.Dumps(value)}");
			}
		}
		return diffs;
	}

	public static List<Finding> CheckRulesets(Api gh, string repo, Standard std, OrderedDictionary<string, string> workflows, string defaultBranch,
			bool removeLegacy, string scope = "full")
	{
		JsonNode? summaries;
		try
		{
			summaries = gh("GET", $"repos/{repo}/rulesets?includes_parents=false");
		}
		catch (ApiError error) when (error.Status is 403 or 404)
		{
			return [new("ruleset main-rules", Unavailable, "rulesets need GitHub Pro (or a public repo)")];
		}
		var available = workflows.Values.Where(text => RunsOnEveryPr(text, defaultBranch)).SelectMany(text => WorkflowFacts(text).Names).ToHashSet();
		List<Finding> findings = [];
		JsonNode? main = null;
		var mainName = J.Req(std.Ruleset, "name");
		foreach (var summary in Py.Items(summaries))
		{
			var name = J.Req(summary, "name");
			if (Py.Eq(name, mainName))
				main = gh("GET", $"repos/{repo}/rulesets/{Py.Str(J.Req(summary, "id"))}");
			else if (Py.Items(J.Req(std.Legacy, "rulesets")).Any(legacy => Py.Eq(legacy, name)))
			{
				var id = Py.Str(J.Req(summary, "id"));
				findings.Add(new($"ruleset {Py.Str(name)}", Legacy, "legacy ruleset; --remove-legacy deletes it",
						removeLegacy ? () => gh("DELETE", $"repos/{repo}/rulesets/{id}") : null));
			}
			else
				findings.Add(new($"ruleset {Py.Str(name)}", Info, "not managed; left as it is"));
		}
		List<JsonNode?> liveRequired = [];
		foreach (var rule in Py.Items(Py.Get(J.B(main) ? main : new JsonObject(), "rules", new JsonArray())))
		{
			if (J.S(J.Req(rule, "type")) != "required_status_checks")
				continue;
			var parameters = Py.Get(rule, "parameters");
			foreach (var check in Py.Items(Py.Get(J.B(parameters) ? parameters : new JsonObject(), "required_status_checks", new JsonArray())))
				liveRequired.Add(J.Req(check, "context"));
		}
		var (want, missing, kept) = WantedRuleset(std, available, liveRequired);
		if (main is null)
			findings.Insert(0, new("ruleset main-rules", Drift, "missing → created", () => gh("POST", $"repos/{repo}/rulesets", want)));
		else
		{
			var diffs = RulesetDifferences(main, want);
			if (J.S(Py.Get(main, "enforcement")) != "active")
				diffs.Insert(0, $"SUSPENDED (enforcement {Py.Str(Py.Get(main, "enforcement"))}); --fix restores it");
			if (diffs.Count > 0)
			{
				var id = Py.Str(J.Req(main, "id"));
				findings.Insert(0, new("ruleset main-rules", Drift, string.Join("; ", diffs), () => gh("PUT", $"repos/{repo}/rulesets/{id}", want)));
			}
			else
				findings.Insert(0, new("ruleset main-rules", OK, "matches the standard"));
		}
		if (kept.Count > 0)
			findings.Add(new("required checks", Info, "kept outside the standard while a workflow still reports them: " + string.Join(", ", kept)));
		// The Factory carries no .NET workflows (ADR 0009), so some of the standard's checks can never be reported there.
		if (missing.Count > 0 && scope == "factory")
			findings.Add(new("required checks", Info, "no workflow on the default branch reports " + string.Join(", ", missing) + "; the factory scope does not require them"));
		else if (missing.Count > 0)
			findings.Add(new("required checks", Manual, "no workflow on the default branch reports " + string.Join(", ", missing)
					+ "; re-Apply the Template, then run --fix again to require them"));
		// Classic protection on the default branch: a leftover once main-rules is active.
		try
		{
			gh("GET", $"repos/{repo}/branches/{defaultBranch}/protection");
		}
		catch (ApiError error) when (error.Status is 403 or 404)
		{
			return findings;
		}
		var active = main is not null && J.S(Py.Get(main, "enforcement")) == "active";
		var detail = active ? "classic protection alongside main-rules; --remove-legacy deletes it"
				: "classic protection; --remove-legacy deletes it once main-rules is active (run --fix twice)";
		findings.Add(new($"branch protection on {defaultBranch}", Legacy, detail,
				removeLegacy && active ? () => gh("DELETE", $"repos/{repo}/branches/{defaultBranch}/protection") : null));
		return findings;
	}

	// The repo's labels by name: the first 100, the one page settings.py read.
	public static OrderedDictionary<string, JsonNode?> LiveLabels(Api gh, string repo)
	{
		OrderedDictionary<string, JsonNode?> labels = [];
		foreach (var label in Py.Items(gh("GET", $"repos/{repo}/labels?per_page=100")))
			labels[Py.Text(J.Req(label, "name"))] = label;
		return labels;
	}

	// Squad is retired everywhere, so its labels go on every --fix, whatever the repo's scope.
	public static List<Finding> CheckRetiredLabels(Api gh, string repo, Standard std, OrderedDictionary<string, JsonNode?> live)
	{
		var prefixes = Py.Items(std.Setting("removed_label_prefixes")).Select(Py.Text).ToList();
		return [.. live.Keys.Order(Py.CodePoints).Where(name => prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
				.Select(name => new Finding($"label {name}", Drift, "retired label → deleted", () => gh("DELETE", $"repos/{repo}/labels/{Quote(name)}")))];
	}

	public static List<Finding> CheckLabels(Api gh, string repo, Standard std, OrderedDictionary<string, JsonNode?> live)
	{
		List<Finding> findings = [];
		foreach (var want in std.Labels)
		{
			var name = Py.Text(J.Req(want, "name"));
			var current = live.GetValueOrDefault(name);
			if (current is null)
				findings.Add(new($"label {name}", Drift, "missing → created", () => gh("POST", $"repos/{repo}/labels", J.Copy(want))));
			else if (Py.Lower(Py.Text(J.Req(current, "color"))) != Py.Lower(Py.Text(J.Req(want, "color")))
					|| !Py.Eq(J.B(Py.Get(current, "description")) ? Py.Get(current, "description") : (JsonNode)"", J.Req(want, "description")))
			{
				var body = new JsonObject { ["color"] = J.Copy(J.Req(want, "color")), ["description"] = J.Copy(J.Req(want, "description")) };
				findings.Add(new($"label {name}", Drift, "color or description → the standard's", () => gh("PATCH", $"repos/{repo}/labels/{Quote(name)}", body)));
			}
			else
				findings.Add(new($"label {name}", OK, "present"));
		}
		return findings;
	}

	// A label name as one path segment: every byte but the unreserved ones percent-encoded, as quote(name, safe="") does.
	public static string Quote(string name) => Uri.EscapeDataString(name);

	// const COPILOT_REVIEW_CAP = <digits>;, a digit being any Unicode decimal digit as Python's \d reads it, one outside the BMP too
	// (the pair is matched here, and Cap checks it is a digit).
	static readonly Regex CopilotReviewCap = new(@"const COPILOT_REVIEW_CAP = ((?:\d|[\uD800-\uDBFF][\uDC00-\uDFFF])+);", RegexOptions.CultureInvariant);

	// PR Auto-Merge waits for a review of the head for at most review_cap rounds (ADR 0002). Threads hold the merge past the cap too
	// (#146). The ruleset leaves thread resolution to pr-automerge.yml, which enforces both. This checks the repo runs the Template's
	// pr-automerge.yml with that cap.
	public static List<Finding> CheckReviewCap(Standard std, OrderedDictionary<string, string> workflows)
	{
		var want = std.Setting("review_cap");
		var text = workflows.Where(pair => FileName(pair.Key) == "pr-automerge.yml").Select(pair => pair.Value).FirstOrDefault();
		var match = CopilotReviewCap.Matches(text ?? "").FirstOrDefault(m => m.Groups[1].Value.EnumerateRunes().All(r => Rune.GetUnicodeCategory(r) == UnicodeCategory.DecimalDigitNumber));
		if (match is not null && Py.Eq(Py.Number(Py.ParseInt(match.Groups[1].Value)), want))
			return [new("review cap", OK, $"pr-automerge.yml stops waiting for a review of the head after {Py.Str(want)} rounds")];
		var found = match is not null ? $"cap {match.Groups[1].Value}" : !string.IsNullOrEmpty(text) ? "no review cap" : "no pr-automerge.yml";
		return [new("review cap", Manual, $"{found}; re-Apply the Template for the {Py.Str(want)}-round cap")];
	}

	public static List<Finding> CheckSecrets(Api gh, string repo, Standard std)
	{
		var names = Py.Items(Py.Get(gh("GET", $"repos/{repo}/actions/secrets"), "secrets", new JsonArray())).Select(secret => J.Req(secret, "name")).ToList();
		return [.. Py.Items(std.Setting("required_secrets")).Select(Py.Text).Select(name => names.Any(have => Py.Eq(have, name))
				? new Finding($"secret {name}", OK, "present")
				: new Finding($"secret {name}", Manual, "missing; add it in the repo's Actions secrets"))];
	}

	// Every Finding for repo, the standard read from folder.
	public static List<Finding> Check(Api gh, string folder, string repo, bool removeLegacy = false, string scope = "full")
	{
		var std = Load(folder, repo);
		var info = gh("GET", $"repos/{repo}");
		var findings = CheckRepoFlags(gh, repo, info, std);
		var labels = LiveLabels(gh, repo);
		if (scope == "repo-settings-only")
		{
			findings.AddRange(CheckRetiredLabels(gh, repo, std, labels));
			findings.Add(new("scope", Info, "repo settings and retired labels only (repos.txt)"));
			return findings;
		}
		var branch = Py.Str(J.Req(info, "default_branch"));
		var workflows = ReadWorkflows(gh, repo, branch);
		findings.AddRange(CheckSecurity(gh, repo, info, std, workflows));
		findings.AddRange(CheckActions(gh, repo, std, workflows));
		findings.AddRange(CheckRulesets(gh, repo, std, workflows, branch, removeLegacy, scope));
		findings.AddRange(CheckReviewCap(std, workflows));
		findings.AddRange(CheckLabels(gh, repo, std, labels));
		findings.AddRange(CheckRetiredLabels(gh, repo, std, labels));
		findings.AddRange(CheckSecrets(gh, repo, std));
		return findings;
	}

	// Set main-rules to disabled, the emergency exit; --fix restores it. The update carries the whole ruleset as it is, changing only
	// enforcement.
	public static bool Suspend(Api gh, string repo)
	{
		foreach (var summary in Py.Items(gh("GET", $"repos/{repo}/rulesets?includes_parents=false")))
		{
			if (J.S(J.Req(summary, "name")) != "main-rules")
				continue;
			var id = Py.Str(J.Req(summary, "id"));
			var live = gh("GET", $"repos/{repo}/rulesets/{id}");
			var body = new JsonObject();
			foreach (var key in new[] { "name", "target", "conditions", "bypass_actors", "rules" })
			{
				if (J.Has(live, key))
					body[key] = J.Copy(J.Get(live, key));
			}
			body["enforcement"] = "disabled";
			gh("PUT", $"repos/{repo}/rulesets/{id}", body);
			return true;
		}
		return false;
	}

	// ── Output ───────────────────────────────────────────────────────────────────

	static void Report(string repo, List<Finding> findings, TextWriter output)
	{
		output.Write($"\n{repo}\n");
		foreach (var finding in findings)
			output.Write($"  {finding.Status,-11} {finding.Area}: {finding.Detail}\n");
	}

	static bool Failing(List<Finding> findings) => findings.Any(finding => FailingStatuses.Contains(finding.Status));

	// The script: its exit code, after writing what it found. api is gh's (GhApi) and folder the one beside the script by default; a test
	// hands in its own.
	public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter err, Api? api = null, string? folder = null)
	{
		var parser = new Arguments("settings.cs", "[-h] [--all] [--fix] [--remove-legacy] [--suspend] [--json] [repos ...]",
						"Check, and with --fix apply, the Baseline's GitHub settings on a repo.")
				.Flag("--all", "every repo in github/repos.txt")
				.Flag("--fix", "apply the standard, then report again")
				.Flag("--remove-legacy", "with --fix, delete the legacy items listed")
				.Flag("--suspend", "disable main-rules (emergency); --fix restores it")
				.Flag("--json", "machine-readable output");
		if (parser.Parse(args, output, err) is int exit)
			return exit;
		folder ??= AppContext.GetData("EntryPointFileDirectoryPath") as string
				?? throw new InvalidOperationException("Settings.Run needs the folder the standard is in when no script file runs");
		var gh = api ?? GhApi();
		bool fix = parser.Has("--fix"), json = parser.Has("--json"), removeLegacy = parser.Has("--remove-legacy");

		var listed = Repos.Read(folder);
		List<string> repos = [.. parser.Positional];
		if (parser.Has("--all"))
			repos.AddRange(listed.Keys.Where(repo => !repos.Contains(repo)).ToList());
		if (repos.Count == 0)
			return parser.Error("name a repo (owner/repo) or pass --all", err);
		if (removeLegacy && !fix)
			return parser.Error("--remove-legacy only acts with --fix", err);
		// A name that would step out of a REST path (. or ..) or isn't GitHub's is refused before any call: a usage error when it was an
		// argument, and exit 1 when it came from the Fleet registry.
		if (repos.FirstOrDefault(repo => !Repos.Name.IsMatch(repo)) is { } bad)
		{
			if (parser.Positional.Contains(bad))
				return parser.Error($"not owner/repo: {bad}", err);
			err.Write($"repos.txt: not owner/repo: {bad}\n");
			return 1;
		}

		if (parser.Has("--suspend"))
		{
			foreach (var repo in repos)
				output.Write($"{repo}: main-rules {(Suspend(gh, repo) ? "disabled; run --fix to restore it" : "not found")}\n");
			return 0;
		}

		OrderedDictionary<string, List<Finding>> results = [];
		var status = 0;
		try
		{
			foreach (var repo in repos)
			{
				var scope = listed.GetValueOrDefault(repo, "full");
				var findings = Check(gh, folder, repo, removeLegacy, scope);
				if (fix)
				{
					var fixes = findings.Where(finding => finding.Fix is not null && finding.Status is Drift or Legacy).ToList();
					if (!json)
					{
						Report(repo, findings, output);
						output.Write($"  applying {fixes.Count} change(s)\n");
					}
					foreach (var finding in fixes)
					{
						finding.Fix!();
						if (!json)
							output.Write($"  applied     {finding.Area}\n");
					}
					findings = Check(gh, folder, repo, removeLegacy: false, scope);
				}
				results[repo] = findings;
				if (Failing(findings))
					status = 1;
				if (!json)
					Report(repo, findings, output);
			}
		}
		catch (StandardError error)
		{
			err.Write($"{error.Message}\n");
			return 1;
		}
		if (json)
		{
			var report = new JsonObject(results.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)new JsonArray([.. pair.Value.Select(finding =>
					(JsonNode)new JsonObject { ["area"] = finding.Area, ["status"] = finding.Status, ["detail"] = finding.Detail })]))));
			output.Write(Py.Dumps(report, indent: 2) + "\n");
		}
		else
			output.Write("\n" + (status == 0 ? "All repos match the standard.\n" : "Not at the standard: see DRIFT, MANUAL and LEGACY above.\n"));
		return status;
	}
}

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
