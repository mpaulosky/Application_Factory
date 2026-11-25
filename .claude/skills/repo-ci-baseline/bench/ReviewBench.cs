// claude-review-bench.cs's logic: score Claude Review against findings Copilot made on the same commits (mpaulosky/Application_Factory#117).
//
//     dotnet run --file bench/claude-review-bench.cs -- [--effort high] [--model claude-opus-5-5] [--prompt-file p.txt]
//                [--tools Read,Glob,Grep,WebFetch] [--only 4b90c96] [--root ~/github]
//     dotnet run --file bench/claude-review-bench.cs -- --rescore bench/results/<run>.json...
//
// Each case in claude-review-cases.json is a commit of a local clone under --root. For each one this checks the commit out in a temporary
// worktree, writes its diff against the case's base, and runs `claude -p` with the prompt and claude_args of the Template's
// claude-review.yml, so it tests what the workflow runs; the flags override one part at a time. The repo's own settings and CLAUDE.md load,
// as in CI; user-level settings and plugins don't.
//
// It is close to the workflow, not identical: it runs the `claude` on PATH, not the Claude Code version the pinned claude-code-action
// installs. The results file records the CLI version, so a rerun on another version is told apart. It takes the case's root Claude Code
// config (.claude/, .mcp.json, CLAUDE.md, CLAUDE.local.md) from the base, as the action does. It loads no settings at all
// (--setting-sources "", standing in for the runner's empty user level), starts no MCP servers and runs git with hooks off, so neither a
// head commit's nor the base's settings, hooks and servers run, nor your own. Nested CLAUDE.md files and .claude/skills in subdirectories
// still come from the head, as the diff Claude reads does.
//
// The source of the actions the case head's review job pins is copied into the temporary directory's work/_actions, where the workflow's
// prompt says the runner keeps it (clones are cached in ~/.cache/claude-review-bench). Claude reads only the case's worktree, the diff and
// that copy, as in CI. Still, only add a case whose commits you trust, and read a results file before committing it. The diff is split into
// pieces by the workflow's own "Write the PR's diff" step, whose Python this runs in the case's worktree with python3 -I, with
// GITHUB_WORKSPACE set so it finds that copy of the actions; the step also writes the context pieces, and the prompt gets the step's lists
// of both. It reads the workflow with YamlDotNet (Yaml.cs) and needs git, tar and python3 (for that step, until the workflow's heredocs are
// C#) and, but for --rescore, claude.
//
// It prints each case's findings and which expected findings they caught, and any finding that cites a line its file doesn't have at the
// head (mpaulosky/Application_Factory#122), and writes the run to bench/results/<timestamp>.json. Matching is by keyword, so read the findings
// too: a miss can be a finding worded another way.
//
// Ported from claude-review-bench.py (ADR 0017's Update for mpaulosky/Application_Factory#161): --rescore prints and writes what the Python did,
// byte for byte (bench/tests/rescore.test.sh), so Py (../github/Py.cs) holds Python's readings of the JSON it prints. An action a head
// pins whose owner, repo or ref is . or .., or starts with -, is skipped rather than copied, where the Python passed it to git and into a path.
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using YamlDotNet.Core;

// What ended the run with a message on stderr and exit 1, as the Python's sys.exit(message) did.
sealed class BenchExit(string message) : Exception(message);

// What made one case fail, which never ends the run: a git, tar or diff step call that failed (the Python's CalledProcessError), or a
// program that isn't there or a step's output that isn't read (its OSError).
sealed class CaseError(string message) : Exception(message);

static class Bench
{
	const string Prog = "claude-review-bench.cs";

	// Git with hooks off: Baseline repos set a relative core.hooksPath, so a worktree's own hooks (a case head's post-checkout) would
	// otherwise run here.
	static readonly string[] Git = ["git", "-c", "core.hooksPath=/dev/null"];

	// A step's remote action: owner/repo[/path]@ref, \w as Python's (a letter, a number or _).
	static readonly Regex Uses = new(@"\A([\p{L}\p{N}_.-]+)/([\p{L}\p{N}_.-]+)(?:/[^@]*)?@([\p{L}\p{N}_.-]+)\z", RegexOptions.CultureInvariant);

	// What Claude Code reads from a project: its settings and hooks, its MCP servers, and its instructions.
	static readonly string[] ProjectConfig = [".claude", ".mcp.json", "CLAUDE.md", "CLAUDE.local.md"];

	// The Template's claude-review.yml, beside the bench's folder.
	public static string Workflow(string here) => Path.GetFullPath(Path.Combine(here, "..", "template", "owned", ".github", "workflows", "claude-review.yml"));

	static string Cache => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "claude-review-bench", "actions");

	// -- The workflow --

	// The prompt and claude_args of the workflow's Review step, the one that reads the diff's first batch.
	public static (string Prompt, string ClaudeArgs) ReviewStep(string workflow)
	{
		var with = J.Req(Step(workflow, "claude_1"), "with");
		return (Py.Text(J.Req(with, "prompt")), Py.Text(J.Req(with, "claude_args")));
	}

	// The Python the review job's "Write the PR's diff" step (id: diff) runs, from its heredoc.
	public static string DiffStep(string workflow)
	{
		var lines = Py.SplitLines(Py.Text(J.Req(Step(workflow, "diff"), "run")));
		var start = lines.FindIndex(line => Py.Strip(line) == "python3 -I - <<'PY'") + 1;
		var end = start == 0 ? -1 : lines.FindIndex(start, line => Py.Strip(line) == "PY");
		if (end < 0)
			throw new BenchExit($"{Prog}: the diff step in {workflow} has no python3 -I - <<'PY' heredoc ending in PY");
		return string.Join("\n", lines[start..end]) + "\n";
	}

	static JsonNode Step(string workflow, string id)
	{
		var jobs = J.Req(Yaml.Load(File.ReadAllText(workflow)), "jobs");
		return Py.Items(J.Req(J.Req(jobs, "review"), "steps")).FirstOrDefault(step => Py.Eq(Py.Get(step, "id"), id))
				?? throw new BenchExit($"{Prog}: no step with id: {id} in {workflow}'s review job");
	}

	// Run the diff step's Python on temp/claude-review/pr.diff in the case's worktree, as the runner does: the pieces under
	// temp/claude-review/diff and context/, and the lists of them the prompt gets (its pieces_1 and context outputs). A case reviews the
	// diff's first batch, as the Review step does; a diff big enough for more batches is reviewed only that far, and the note_1 output that
	// would say so isn't given.
	public static (string Pieces, string? Context) SplitDiff(string script, string temp, string tree, string @base, string head)
	{
		var output = Path.Combine(temp, "github-output");
		File.WriteAllText(output, "");
		Dictionary<string, string> env = new()
		{
			["PATH"] = "/usr/bin:/bin",
			["RUNNER_TEMP"] = temp,
			["GITHUB_OUTPUT"] = output,
			["BASE_SHA"] = @base,
			["HEAD_SHA"] = head,
			["HOME"] = "/nonexistent",
			["GITHUB_WORKSPACE"] = Path.Combine(temp, "work", "repo", "repo"),
			["GIT_CONFIG_GLOBAL"] = "/dev/null",
			["GIT_CONFIG_NOSYSTEM"] = "1",
		};
		Check(Exec(["python3", "-I", "-"], tree, env, Encoding.UTF8.GetBytes(script)), ["python3", "-I", "-"]);
		var written = File.ReadAllText(output);
		var found = Regex.Match(written, @"\Apieces_1<<(EOF_[0-9a-f]+)\n(.*?)\n\1\n", RegexOptions.Singleline | RegexOptions.CultureInvariant);
		if (!found.Success)
			throw new CaseError($"the diff step wrote no pieces_1 output: {Py.Head(written, 500)}");
		var context = Regex.Match(written, @"^context<<(EOF_[0-9a-f]+)\n(.*?)\n\1\n", RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.CultureInvariant);
		return (found.Groups[2].Value, context.Success ? context.Groups[2].Value : null);
	}

	// Fill in the workflow expressions the prompt and args use; pieces is the diff step's pieces_1 output, filled in as the Review step's
	// REVIEW_PIECES env. github.workspace is temp/work/repo/repo, so its ../../_actions is temp/work/_actions, laid out as on a runner.
	public static string Render(string text, string pr, string temp, string? pieces = null, string? context = null)
	{
		if (pieces is not null)
		{
			// The Review step's env: its batch's pieces, and no note, since a case reviews one batch alone.
			text = text.Replace("${{ env.REVIEW_PIECES }}", pieces, StringComparison.Ordinal);
			text = text.Replace("${{ env.REVIEW_BATCH }}\n", "", StringComparison.Ordinal);
		}
		if (context is not null)
			text = text.Replace("${{ steps.diff.outputs.context }}", context, StringComparison.Ordinal);
		// A case reviews its PR's head as a first review would: no thread of it is resolved yet.
		text = text.Replace("${{ steps.resolved.outputs.threads || 'Not known: the threads could not be read.' }}",
				"None: no thread of this PR from Copilot or Claude Review is resolved yet.", StringComparison.Ordinal);
		// And with no accepted findings recorded in its description.
		text = text.Replace("${{ steps.notes.outputs.notes || 'Not known: the description could not be read.' }}",
				"None: the PR's description records no accepted findings.", StringComparison.Ordinal);
		text = text.Replace("${{ github.event.pull_request.number }}", pr, StringComparison.Ordinal);
		text = text.Replace("${{ runner.temp }}", temp, StringComparison.Ordinal);
		text = text.Replace("${{ github.workspace }}", Path.Combine(temp, "work", "repo", "repo"), StringComparison.Ordinal);
		var left = Regex.Matches(text, @"\$\{\{[^}]*\}\}", RegexOptions.CultureInvariant);
		if (left.Count > 0)
			throw new BenchExit($"{Prog}: unrendered expressions: {Py.Repr(new JsonArray([.. left.Select(m => (JsonNode?)m.Value)]))}");
		return text;
	}

	// claude_args with one flag's value replaced, or the flag added.
	public static List<string> Override(List<string> args, string? effort, string? model, string? tools, string? settingSources = null)
	{
		void Put(string flag, string value)
		{
			if (args.IndexOf(flag) is var i and >= 0)
				args[i + 1] = value;
			else
				args.AddRange([flag, value]);
		}
		if (!string.IsNullOrEmpty(effort))
			Put("--effort", effort);
		if (!string.IsNullOrEmpty(model))
			Put("--model", model);
		if (settingSources is not null)
			Put("--setting-sources", settingSources);
		if (!string.IsNullOrEmpty(tools))
		{
			Put("--allowedTools", tools);
			if (args.IndexOf("--disallowedTools") is var i and >= 0)
			{
				var allowed = tools.Split(',').ToHashSet();
				args[i + 1] = string.Join(",", args[i + 1].Split(',').Where(tool => !allowed.Contains(tool)));
			}
		}
		return args;
	}

	// shlex.split(text): words split at spaces, tabs and line breaks, with '...' taken as it is, "..." with \" and \\ escaped in it, and a
	// \ outside quotes escaping the character after it. An unclosed quote or a \ at the end throws, as shlex's ValueError.
	public static List<string> ShlexSplit(string text)
	{
		List<string> words = [];
		StringBuilder? word = null;
		for (var i = 0; i < text.Length; i++)
		{
			var c = text[i];
			if (c is ' ' or '\t' or '\r' or '\n')
			{
				if (word is not null)
					words.Add(word.ToString());
				word = null;
				continue;
			}
			word ??= new StringBuilder();
			if (c == '\\')
			{
				if (++i >= text.Length)
					throw new FormatException("No escaped character");
				word.Append(text[i]);
			}
			else if (c == '\'')
			{
				var close = text.IndexOf('\'', i + 1);
				if (close < 0)
					throw new FormatException("No closing quotation");
				word.Append(text, i + 1, close - i - 1);
				i = close;
			}
			else if (c == '"')
			{
				for (i++; ; i++)
				{
					if (i >= text.Length)
						throw new FormatException("No closing quotation");
					if (text[i] == '"')
						break;
					if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\')
						i++;
					word.Append(text[i]);
				}
			}
			else
				word.Append(c);
		}
		if (word is not null)
			words.Add(word.ToString());
		return words;
	}

	// -- The case's tree --

	// (owner, repo, ref) for each remote action the review job uses, in order and without repeats. An owner, repo or ref that is . or ..,
	// or starts with -, is left out: it would step out of the folder it is copied to, or read as an option to git.
	public static List<(string Owner, string Repo, string Ref)> PinnedActions(string workflowText)
	{
		JsonNode? steps;
		try
		{
			steps = J.Req(J.Req(J.Req(Yaml.Load(workflowText), "jobs"), "review"), "steps");
		}
		catch (Exception error) when (error is YamlException or KeyNotFoundException)
		{
			return [];
		}
		List<(string, string, string)> found = [];
		foreach (var step in steps as JsonArray ?? new JsonArray())
		{
			if (step is not JsonObject || Uses.Match(Py.Str(Py.Get(step, "uses", ""))) is not { Success: true } match)
				continue;
			var action = (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
			if (new[] { action.Item1, action.Item2, action.Item3 }.Any(part => part is "." or ".." || part.StartsWith('-')))
				continue;
			if (!found.Contains(action))
				found.Add(action);
		}
		return found;
	}

	// Copy the source of the actions the head's review job pins into actionsDir, as a runner lays it out.
	static void CopyActions(string tree, string head, string actionsDir)
	{
		var shown = Exec([.. Git, "-C", tree, "show", $"{head}:.github/workflows/claude-review.yml"]);
		foreach (var (owner, repo, @ref) in PinnedActions(shown.Code == 0 ? shown.Text : ""))
		{
			var cache = Path.Combine(Cache, owner, repo);
			if (!Path.Exists(cache))
				Check([.. Git, "clone", "-q", "--bare", "--filter=blob:none", $"https://github.com/{owner}/{repo}.git", cache]);
			if (Exec([.. Git, "-C", cache, "cat-file", "-e", $"{@ref}^{{commit}}"]).Code != 0)
				Check([.. Git, "-C", cache, "fetch", "-q", "origin", @ref]);
			var dest = Path.Combine(actionsDir, owner, repo, @ref);
			Directory.CreateDirectory(dest);
			var archive = Check([.. Git, "-C", cache, "archive", @ref]).Stdout;
			Check(Exec(["tar", "-x", "-C", dest], input: archive), ["tar", "-x", "-C", dest]);
		}
	}

	// Replace the head's root Claude Code config with the base's, so a case's own settings, hooks and MCP servers never load.
	// --strict-mcp-config (in RunCase) also keeps any MCP server from starting. A link is removed itself, never what it points to.
	public static void UseBaseSettings(string tree, string @base)
	{
		foreach (var name in ProjectConfig)
		{
			var path = Path.Combine(tree, name);
			var info = new FileInfo(path);
			if (info.LinkTarget is not null)
				info.Delete();
			else if (Directory.Exists(path))
				Directory.Delete(path, recursive: true);
			else if (File.Exists(path))
				File.Delete(path);
			var listed = Check([.. Git, "-C", tree, "ls-tree", @base, "--", name]).Text;
			if (Py.Strip(listed).Length > 0)
				Check([.. Git, "-C", tree, "checkout", @base, "--", name]);
		}
	}

	// -- A case's review --

	// One case's review, or an error outcome: a failed case never ends the run.
	public static JsonObject RunCase(JsonNode @case, string prompt, string claudeArgs, (string? Effort, string? Model, string? Tools) overrides,
			string root, long timeout, string? split)
	{
		var repo = Path.Combine(root, Py.Text(J.Req(@case, "repo")));
		var (head, @base, pr) = (Py.Text(J.Req(@case, "head")), Py.Text(J.Req(@case, "base")), Py.Str(J.Req(@case, "pr")));
		var started = Stopwatch.StartNew();
		JsonObject Failed(string error) => new() { ["error"] = error, ["seconds"] = Seconds(started) };
		var temp = Directory.CreateTempSubdirectory("claude-review-bench-").FullName;
		var tree = Path.Combine(temp, "tree");
		var added = false;
		Ran done;
		List<string> args;
		try
		{
			Check([.. Git, "-C", repo, "worktree", "add", "-q", "--detach", tree, head]);
			added = true;
			UseBaseSettings(tree, @base);
			Directory.CreateDirectory(Path.Combine(temp, "work", "repo", "repo"));
			CopyActions(tree, head, Path.Combine(temp, "work", "_actions"));
			// The runner's plain git diff: no external diff tool or prefix settings from the global config, and any file encoding read
			// without failing.
			var diff = Check([.. Git, "-c", "diff.noprefix=false", "-c", "diff.mnemonicPrefix=false", "-c", "core.quotePath=true", "-C", tree,
					"diff", "--no-color", "--no-ext-diff", $"{@base}...{head}"]).Text;
			Directory.CreateDirectory(Path.Combine(temp, "claude-review"));
			File.WriteAllText(Path.Combine(temp, "claude-review", "pr.diff"), diff);
			// The workflow's own split of the diff into pieces, and the list the prompt names them in.
			var (pieces, context) = split is null ? (null, null) : SplitDiff(split, temp, tree, @base, head);
			args = Override(ShlexSplit(Render(claudeArgs, pr, temp)), overrides.Effort, overrides.Model, overrides.Tools);
			// The runner's user level has no settings; yours mustn't load in its place.
			args = Override(args, null, null, null, settingSources: "");
			List<string> argv = ["claude", "-p", Render(prompt, pr, temp, pieces, context), .. args,
					"--output-format", "stream-json", "--verbose", "--strict-mcp-config", "--no-session-persistence"];
			done = Exec(argv, tree, timeout: timeout);
		}
		catch (TimeoutException)
		{
			return Failed($"timed out after {timeout}s");
		}
		catch (Exception error) when (error is CaseError or IOException or UnauthorizedAccessException)
		{
			// A missing clone or commit, or an action that can't be fetched.
			return Failed(error.Message);
		}
		finally
		{
			if (added)
				Exec([.. Git, "-C", repo, "worktree", "remove", "--force", tree], inherit: true);
			Directory.Delete(temp, recursive: true);
		}
		return Outcome(done, args, Seconds(started));
	}

	// What a finished claude call gave: the review with its cost, turns and reads, or the case's error. The shape post_claude_review.py accepts is
	// the only review taken; anything else is this case's error, not the run's.
	public static JsonObject Outcome(Ran done, IReadOnlyList<string> args, long seconds)
	{
		if (done.Code != 0)
			return new() { ["error"] = Py.Tail(done.Stderr.Length > 0 ? done.Stderr : done.Text, 2000), ["seconds"] = seconds };
		JsonNode? data, review;
		JsonArray reads;
		try
		{
			(data, reads) = Transcript(done.Text);
			review = Py.Get(data, "structured_output");
			if (review is null)
				review = JsonNode.Parse(J.B(Py.Get(data, "result")) ? Py.Text(Py.Get(data, "result")) : "{}");
		}
		catch (Exception error) when (error is JsonException or InvalidOperationException)
		{
			return new() { ["error"] = $"no structured review ({error.Message}): {Py.Tail(done.Text, 2000)}", ["seconds"] = seconds };
		}
		if (!(review is JsonObject && J.Get(review, "findings") is JsonArray findings && findings.All(f => f is JsonObject)))
			return new() { ["error"] = $"review isn't an object with a list of findings: {Py.Head(Py.Str(review), 2000)}", ["seconds"] = seconds };
		var outcome = new JsonObject
		{
			["review"] = J.Copy(review),
			["seconds"] = seconds,
			["cost_usd"] = J.Copy(Py.Get(data, "total_cost_usd")),
			["turns"] = J.Copy(Py.Get(data, "num_turns")),
			["reads"] = reads,
		};
		if (ActionRejects(outcome["turns"], args))
			outcome["action_rejects"] = true;
		return outcome;
	}

	// claude-code-action rejects a result whose num_turns is over --max-turns, after it has run; Claude Code itself counts round-trips
	// instead, so a review it lets finish can still be one the workflow would never post. With no --max-turns, or a turn count that isn't an
	// int, nothing is rejected.
	public static bool ActionRejects(JsonNode? turns, IReadOnlyList<string> args)
	{
		var at = args.ToList().IndexOf("--max-turns");
		BigInteger? cap = at >= 0 ? BigInteger.Parse(args[at + 1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : null;
		return cap is not null && Py.Int(turns) is { } count && count > cap;
	}

	// round(): to the nearest second, a half to the even one.
	static long Seconds(Stopwatch started) => (long)Math.Round(started.Elapsed.TotalSeconds, MidpointRounding.ToEven);

	// From `claude -p --output-format stream-json`: the result message, and the files each turn of tool calls read (a list per assistant
	// message that called a tool), so a run shows what one turn of reads covered. A stream with no result message throws JsonException.
	public static (JsonNode Result, JsonArray Reads) Transcript(string stream)
	{
		JsonNode? result = null;
		OrderedDictionary<string, JsonArray> turns = [];
		// JSON Lines: a line ends at \n alone. JSON.stringify writes U+2028, U+2029 and U+0085 raw inside a string, which str.splitlines() (the
		// Python's split, and Py.SplitLines) also broke at, cutting such a review in half.
		foreach (var line in stream.Split('\n'))
		{
			if (Py.Strip(line).Length == 0)
				continue;
			var message = JsonNode.Parse(line);
			var type = Py.Get(message, "type");
			if (Py.Eq(type, "result"))
				result = message;
			else if (Py.Eq(type, "assistant"))
			{
				// Claude Code streams an API message's blocks as separate messages with the same id.
				var body = Py.Get(message, "message", new JsonObject());
				var calls = Py.Items(Py.Get(body, "content", new JsonArray())).Where(block => block is JsonObject && Py.Eq(Py.Get(block, "type"), "tool_use")).ToList();
				if (calls.Count == 0)
					continue;
				// The id, else the number of turns so far, as dict keys: Repr tells the string "1" from the number 1, as the dict did.
				var id = Py.Get(body, "id");
				var key = J.B(id) ? Py.Repr(id) : turns.Count.ToString(CultureInfo.InvariantCulture);
				if (!turns.TryGetValue(key, out var turn))
					turns[key] = turn = [];
				foreach (var call in calls)
				{
					var input = Py.Get(call, "input", new JsonObject());
					turn.Add(Py.Str(Or(Py.Get(input, "file_path"), Py.Get(input, "pattern"), Py.Get(call, "name"))));
				}
			}
		}
		if (result is null)
			throw new JsonException($"no result message: {Py.Tail(stream, 200)}");
		return (result, new JsonArray([.. turns.Values]));
	}

	// a or b or c: the first that is true, else the last.
	static JsonNode? Or(params JsonNode?[] values) => values.FirstOrDefault(J.B) ?? values[^1];

	// -- Scoring --

	// The found findings on one of the finding's paths that mention every word group of it. Words match whole: "tab" doesn't match "table",
	// nor "cap" "escape".
	public static List<JsonNode?> Caught(JsonNode? finding, IEnumerable<JsonNode?> findingsFound)
	{
		static bool Mentions(string text, string word) =>
				Regex.IsMatch(text, "(?<![a-z0-9])" + Regex.Escape(Py.Lower(word)) + "(?![a-z0-9])", RegexOptions.CultureInvariant);
		bool Matches(string text)
		{
			text = Py.Lower(text);
			return Py.Items(J.Req(finding, "match")).All(group => Py.Items(group).Any(word => Mentions(text, Py.Text(word))));
		}
		bool OnPath(JsonNode? found)
		{
			// A whole path component: release_post.py isn't tests/test_release_post.py.
			var path = Py.Str(Py.Get(found, "path", ""));
			var paths = Py.Get(finding, "paths");
			return !J.B(paths) || Py.Items(paths).Any(p => path == Py.Text(p) || path.EndsWith("/" + Py.Text(p), StringComparison.Ordinal));
		}
		return [.. findingsFound.Where(f => OnPath(f) && Matches(Py.Str(Py.Get(f, "body", ""))))];
	}

	// {finding: caught} for the case's expected findings, and (required caught, required total).
	public static (OrderedDictionary<string, bool> Hits, int Got, int OutOf) Score(JsonNode spec, JsonNode @case, JsonNode outcome)
	{
		var found = Found(outcome);
		var findings = J.Req(spec, "findings");
		OrderedDictionary<string, bool> hits = [];
		var expect = Py.Items(J.Req(@case, "expect")).Select(Py.Text).ToList();
		foreach (var key in expect)
			hits[key] = Caught(J.Req(findings, key), found).Count > 0;
		var required = expect.Where(key => !J.B(Py.Get(J.Req(findings, key), "optional"))).ToList();
		return (hits, required.Count(key => hits[key]), required.Count);
	}

	// (outcome.get("review") or {}).get("findings", []).
	static List<JsonNode?> Found(JsonNode outcome)
	{
		var review = Py.Get(outcome, "review");
		return [.. Py.Items(Py.Get(J.B(review) ? review : new JsonObject(), "findings", new JsonArray()))];
	}

	// The "path:line" of each finding that cites a line its file doesn't have at head (mpaulosky/Application_Factory#122). A file that isn't in
	// head counts too. Null when the clone isn't there to check.
	public static JsonArray? BadLines(string repo, string head, IEnumerable<JsonNode?> findings)
	{
		if (!Path.Exists(Path.Combine(repo, ".git")))
			return null;
		Dictionary<string, BigInteger?> lengths = [];
		var bad = new JsonArray();
		foreach (var f in findings)
		{
			var path = Py.Str(Py.Get(f, "path", ""));
			var line = Py.Get(f, "line");
			if (!lengths.TryGetValue(path, out var length))
			{
				var shown = Exec([.. Git, "-C", repo, "show", $"{head}:{path}"]);
				var content = shown.Stdout;
				lengths[path] = length = shown.Code != 0 ? null
						: content.Count(b => b == (byte)'\n') + (content.Length > 0 && content[^1] != (byte)'\n' ? 1 : 0);
			}
			if (length is null || Py.Int(line) is not { } number || !(1 <= number && number <= length))
				bad.Add($"{path}:{Py.Str(line)}");
		}
		return bad;
	}

	// Text from the model or a call, shown with each control character but a tab or newline, and each format or line or paragraph
	// separator character (a bidi override, U+2028), written in repr()'s \xNN or \uNNNN form, so an escape sequence or a reordering a
	// head led the review to write can't reach the terminal. The Python printed them as they were.
	public static string Shown(string text)
	{
		if (!text.Any(Hidden))
			return text;
		var shown = new StringBuilder(text.Length + 8);
		foreach (var c in text)
			shown.Append(!Hidden(c) ? c.ToString() : c <= '\u00ff' ? $"\\x{(int)c:x2}" : $"\\u{(int)c:x4}");
		return shown.ToString();
	}

	static bool Hidden(char c) =>
		c is not '\t' and not '\n' && char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format
			or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

	public static void Report(JsonNode spec, JsonNode @case, JsonNode outcome, OrderedDictionary<string, bool> hits, TextWriter output)
	{
		var found = Found(outcome);
		var findings = J.Req(spec, "findings");
		output.Write($"== {Py.Str(J.Req(@case, "repo"))}#{Py.Str(J.Req(@case, "pr"))} {Py.Str(J.Req(@case, "head"))}\n");
		if (J.Has(outcome, "error"))
			output.Write($"   error: {Shown(Py.Str(J.Req(outcome, "error")))}\n");
		var copilot = J.Has(@case, "copilot_threads") ? $" (Copilot opened {Py.Str(J.Req(@case, "copilot_threads"))} thread(s))" : "";
		output.Write($"   {Shown(Py.Str(J.Get(outcome, "seconds")))}s, {Shown(Py.Str(J.Get(outcome, "turns")))} turns, ${Shown(Py.Str(J.Get(outcome, "cost_usd")))}; "
				+ $"{found.Count} finding(s){copilot}\n");
		if (J.B(J.Get(outcome, "action_rejects")))
			output.Write("   claude-code-action would reject this review: its num_turns is over --max-turns\n");
		if (J.Get(outcome, "reads") is { } reads)
		{
			var turns = Py.Items(reads).Select(turn => Py.Items(turn).Count()).ToList();
			output.Write($"   {turns.Count} turn(s) of tool calls, of [{string.Join(", ", turns)}] call(s)\n");
		}
		foreach (var (key, hit) in hits)
		{
			var optional = J.B(Py.Get(J.Req(findings, key), "optional")) ? " (optional)" : "";
			output.Write($"   {(hit ? "caught" : "missed")} {key}{optional}: {Py.Str(J.Req(J.Req(findings, key), "about"))}\n");
		}
		foreach (var f in found)
			output.Write($"   - {Shown(Py.Str(Py.Get(f, "path")))}:{Shown(Py.Str(Py.Get(f, "line")))}: {Shown(Py.Head(string.Join(" ", Py.Split(Py.Str(Py.Get(f, "body", "")))), 300))}\n");
		if (J.Get(outcome, "bad_lines") is { } badLines && J.B(badLines))
		{
			var lines = Py.Items(badLines).Select(Py.Text).ToList();
			output.Write($"   {lines.Count} finding(s) on a line the file doesn't have: {Shown(string.Join(", ", lines))}\n");
		}
	}

	// Score a saved run again with the current cases file, and write the new score back into it.
	public static void Rescore(JsonNode spec, string path, string root, TextWriter output)
	{
		var saved = JsonNode.Parse(ReadText(path))!;
		Dictionary<string, JsonObject> cases = [];
		foreach (var c in Py.Items(J.Req(spec, "cases")))
			cases[Py.Repr(J.Req(c, "head"))] = (JsonObject)c!;
		var (got, outOf) = (0, 0);
		foreach (var result in Py.Items(J.Req(saved, "cases")))
		{
			// {**result, **cases.get(result["head"], {})}: the result's keys, the case's values over them.
			var @case = (JsonObject)J.Copy(result)!;
			if (cases.TryGetValue(Py.Repr(J.Req(result, "head")), out var known))
			{
				foreach (var (key, value) in known)
					@case[key] = J.Copy(value);
			}
			var (hits, caught, required) = Score(spec, @case, result!);
			if (J.Has(result, "review"))
				result!["bad_lines"] = BadLines(Path.Combine(root, Py.Text(J.Req(@case, "repo"))), Py.Str(J.Req(@case, "head")),
						Py.Items(Py.Get(J.Req(result, "review"), "findings", new JsonArray())));
			Report(spec, @case, result!, hits, output);
			result!["caught"] = Hits(hits);
			got += caught;
			outOf += required;
		}
		saved["score"] = new JsonArray(got, outOf);
		File.WriteAllText(path, Py.Dumps(saved, indent: 2) + "\n");
		output.Write($"\nscore: {got}/{outOf} required findings caught (written back to {Name(path)})\n");
	}

	static JsonObject Hits(OrderedDictionary<string, bool> hits) => new(hits.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value)));

	// pathlib's name: the last part of the path, a trailing / dropped.
	static string Name(string path) => Path.GetFileName(path.TrimEnd('/'));

	// A prompt file's text with its line endings read as read_text() read them: \r\n and a lone \r are \n, so Render finds the workflow's lines
	// in a prompt written on Windows.
	public static string ReadPrompt(string path) => ReadText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

	// read_text(): UTF-8, a byte that isn't throwing.
	static string ReadText(string path) => new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));

	// -- The command --

	// The options, as the Python declared them to argparse (BenchPortTests holds the two equal).
	public static Arguments Parser() => new Arguments(Prog, "[-h] [--effort EFFORT] [--model MODEL] [--prompt-file PROMPT_FILE] [--tools TOOLS] [--only ONLY] [--set SET] "
			+ "[--root ROOT] [--timeout TIMEOUT] [--label LABEL] [--rescore RESCORE [RESCORE ...]]",
			"Score Claude Review against findings Copilot made on the same commits (mpaulosky/Application_Factory#117).")
			.Option("--effort", "replace --effort (low, medium, high, xhigh, max)")
			.Option("--model", "replace --model")
			.Option("--prompt-file", "replace the prompt")
			.Option("--tools", "replace --allowedTools, and drop these from --disallowedTools")
			.Option("--only", "run only the case with this head (in either set)")
			.Option("--set", "which cases: recall (findings to catch), noise (ordinary PRs) or large (a diff too long for one Read)")
			.Option("--root", "where the cases' clones live")
			.IntOption("--timeout", "seconds per case")
			.Option("--label", "a name for this run in its results file")
			.ManyOption("--rescore", "score saved results files again with the current cases file, without running Claude, and write the new scores back");

	// The command: its exit code, a BenchExit's message on stderr and exit 1. here is the bench's folder, the entry file's by default.
	public static int Run(string[] argv, TextWriter output, TextWriter err, string? here = null)
	{
		here ??= AppContext.GetData("EntryPointFileDirectoryPath") as string
				?? throw new InvalidOperationException("the bench needs its folder when no script file runs");
		try
		{
			return Command(argv, here, output, err);
		}
		catch (BenchExit exit)
		{
			err.Write(exit.Message + "\n");
			return 1;
		}
	}

	static int Command(string[] argv, string here, TextWriter output, TextWriter err)
	{
		var parser = Parser();
		if (parser.Parse(argv, output, err) is int exit)
			return exit;
		if (parser.Positional.Count > 0)
			return parser.Error($"unrecognized arguments: {string.Join(" ", parser.Positional)}", err);
		var root = parser.Values.GetValueOrDefault("--root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "github");
		var timeout = parser.Int("--timeout", 1800);
		var label = parser.Values.GetValueOrDefault("--label") ?? "";

		var spec = JsonNode.Parse(ReadText(Path.Combine(here, "claude-review-cases.json")))!;
		if (parser.Lists.TryGetValue("--rescore", out var rescore))
		{
			foreach (var path in rescore)
			{
				output.Write($"#### {Name(path)}\n");
				Rescore(spec, path, root, output);
			}
			return 0;
		}

		if (Which("claude") is null)
			throw new BenchExit($"{Prog}: claude isn't on PATH");
		var workflow = Workflow(here);
		var (prompt, claudeArgs) = ReviewStep(workflow);
		var split = DiffStep(workflow);
		if (parser.Values.GetValueOrDefault("--prompt-file") is { } promptFile)
			prompt = ReadPrompt(promptFile);
		(string? Effort, string? Model, string? Tools) overrides =
				(parser.Values.GetValueOrDefault("--effort"), parser.Values.GetValueOrDefault("--model"), parser.Values.GetValueOrDefault("--tools"));
		var results = Path.Combine(here, "results");
		Directory.CreateDirectory(results);
		var file = Path.Combine(results, $"{DateTime.Now.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}{(label.Length > 0 ? "-" + label : "")}.json");
		var run = new JsonObject
		{
			["label"] = label,
			["claude_version"] = Py.Strip(Exec(["claude", "--version"]).Text),
			["claude_args"] = claudeArgs,
			["overrides"] = new JsonArray(overrides.Effort, overrides.Model, overrides.Tools),
			["prompt"] = prompt,
			["score"] = new JsonArray(0, 0),
			["cases"] = new JsonArray(),
		};

		// --only names one case, whichever set it's in.
		var only = parser.Values.GetValueOrDefault("--only") ?? "";
		var set = parser.Values.GetValueOrDefault("--set") ?? "recall";
		var selected = Py.Items(J.Req(spec, "cases")).Where(c => only.Length > 0
				? Py.Text(J.Req(c, "head")).StartsWith(only, StringComparison.Ordinal)
				: Py.Eq(Py.Get(c, "set", "recall"), set)).ToList();
		if (selected.Count == 0)
			throw new BenchExit($"{Prog}: no case matches {(only.Length > 0 ? "--only " + only : "--set " + set)}");
		var (got, outOf) = (0, 0);
		foreach (var @case in selected)
		{
			var outcome = RunCase(@case!, prompt, claudeArgs, overrides, root, timeout, split);
			if (J.Has(outcome, "review"))
				outcome["bad_lines"] = BadLines(Path.Combine(root, Py.Text(J.Req(@case, "repo"))), Py.Text(J.Req(@case, "head")),
						Py.Items(J.Req(J.Req(outcome, "review"), "findings")));
			var (hits, caught, required) = Score(spec, @case!, outcome);
			Report(spec, @case!, outcome, hits, output);
			// {**case, **outcome, "caught": hits}
			var entry = (JsonObject)J.Copy(@case)!;
			foreach (var (key, value) in outcome)
				entry[key] = J.Copy(value);
			entry["caught"] = Hits(hits);
			((JsonArray)run["cases"]!).Add(entry);
			(got, outOf) = (got + caught, outOf + required);
			run["score"] = new JsonArray(got, outOf);
			// Written after every case, so a long run that stops part way keeps what it has.
			File.WriteAllText(file, Py.Dumps(run, indent: 2) + "\n");
		}

		output.Write($"\nscore: {got}/{outOf} required findings caught\n");
		output.Write($"wrote {file}\n");
		return 0;
	}

	// -- Processes --

	// One finished call: its exit code, stdout as bytes and as UTF-8 text (a byte that isn't UTF-8 read as U+FFFD), and stderr.
	public readonly record struct Ran(int Code, byte[] Stdout, string Stderr)
	{
		public string Text => Encoding.UTF8.GetString(Stdout);
	}

	// The program found in PATH's absolute folders, as Gh.Which finds gh, else null.
	static string? Which(string name)
	{
		try
		{
			return Gh.Which(name, Environment.GetEnvironmentVariable("PATH"));
		}
		catch (FileNotFoundException)
		{
			return null;
		}
	}

	// Runs argv (its program found by Which) in cwd, with env as its whole environment when given, input on its stdin, and its output
	// captured unless inherit; past timeout seconds it is killed with its children and TimeoutException thrown. One that exits within it
	// but leaves a child holding its pipes throws TimeoutException once the time is up, and that child, no longer its child, runs on (as
	// under the Python, where communicate() then waited for it). A program that isn't there, or can't be run, throws CaseError.
	public static Ran Exec(IReadOnlyList<string> argv, string? cwd = null, Dictionary<string, string>? env = null, byte[]? input = null, long? timeout = null,
			bool inherit = false)
	{
		var start = new ProcessStartInfo(Which(argv[0]) ?? throw new CaseError($"{argv[0]} isn't on PATH (only its absolute folders are searched)"))
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = !inherit,
			RedirectStandardError = !inherit,
			UseShellExecute = false,
			StandardErrorEncoding = inherit ? null : Encoding.UTF8,
		};
		if (cwd is not null)
			start.WorkingDirectory = cwd;
		if (env is not null)
		{
			start.Environment.Clear();
			foreach (var (key, value) in env)
				start.Environment[key] = value;
		}
		foreach (var arg in argv.Skip(1))
			start.ArgumentList.Add(arg);
		Process process;
		try
		{
			process = Process.Start(start)!;
		}
		catch (Win32Exception error)
		{
			// A file in PATH that can't be run (no #! line, a binary for another machine): this case's error, as the Python's OSError was.
			throw new CaseError($"{argv[0]} couldn't be run: {error.Message}");
		}
		using var _ = process;
		var write = Task.Run(() =>
		{
			try
			{
				if (input is not null)
					process.StandardInput.BaseStream.Write(input);
				process.StandardInput.Close();
			}
			catch (IOException)
			{
			}
		});
		var stdout = inherit ? Task.FromResult(Array.Empty<byte>()) : Task.Run(() =>
		{
			using var bytes = new MemoryStream();
			process.StandardOutput.BaseStream.CopyTo(bytes);
			return bytes.ToArray();
		});
		var stderr = inherit ? Task.FromResult("") : process.StandardError.ReadToEndAsync();
		var limit = Limit(timeout);
		var clock = Stopwatch.StartNew();
		if (!process.WaitForExit(limit))
		{
			process.Kill(entireProcessTree: true);
			process.WaitForExit();
			throw new TimeoutException();
		}
		process.WaitForExit();
		// The pipes close when every process holding them has, so a child the call left running keeps them open past its exit: the time
		// left bounds them too, as Python's communicate(timeout=) did.
		if (limit == Timeout.InfiniteTimeSpan)
			Task.WaitAll(write, stdout, stderr);
		else if (!Task.WaitAll([write, stdout, stderr], TimeSpan.Zero > limit - clock.Elapsed ? TimeSpan.Zero : limit - clock.Elapsed))
			throw new TimeoutException();
		return new Ran(process.ExitCode, stdout.Result, stderr.Result);
	}

	// A call's timeout as WaitForExit takes it: none without one, and otherwise its seconds between 0 and the most whole seconds an int of
	// milliseconds holds, past which WaitForExit throws.
	public static TimeSpan Limit(long? timeout) =>
		timeout is long seconds ? TimeSpan.FromSeconds(Math.Clamp(seconds, 0, int.MaxValue / 1000)) : Timeout.InfiniteTimeSpan;

	// Run argv, failing the case when it exits other than 0, as check=True did.
	public static Ran Check(IReadOnlyList<string> argv) => Check(Exec(argv), argv);

	static Ran Check(Ran ran, IReadOnlyList<string> argv) => ran.Code == 0 ? ran
			: throw new CaseError($"Command {Py.Repr(new JsonArray([.. argv.Select(a => (JsonNode?)a)]))} returned non-zero exit status {ran.Code}. {ran.Stderr}".Trim());
}
