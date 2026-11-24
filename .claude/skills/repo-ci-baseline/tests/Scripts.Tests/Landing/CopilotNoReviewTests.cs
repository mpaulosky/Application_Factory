using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

// The spellings of "Copilot's note isn't a review" agree.
//
// PR Auto-Merge (JS), claude-review.yml (two jq filters) and the landing scripts (C#, as the Python before them) each decide whether a
// Copilot review is its quota note. If they disagreed, one would count a commit as reviewed that another doesn't: Claude Review would
// skip a head the merge gate still waits on, or the merge gate would count a note claude-review.yml re-reviews. This runs each real
// pattern on the same bodies.
public class CopilotNoReviewTests
{
	const string Login = "copilot-pull-request-reviewer[bot]";

	const string Note = "Copilot was unable to review this pull request because the user who requested the review has reached "
			+ "their quota limit.";

	static readonly (string Name, string Body, bool IsNote)[] Bodies =
	[
		("the quota note", Note, true),
		("the note after leading whitespace", "\n  " + Note, true),
		// Copilot's note when every changed file is one it excludes (a lockfile-only PR) still counts, as it did.
		("the no-reviewable-files note", "Copilot wasn't able to review any files in this pull request.", false),
		("a real review", "## Pull request overview\n\nThis PR fixes the cap.", false),
		("a real review that quotes the note on a later line", "## Pull request overview\n\nCopilot was unable to review 2 files:\n- a.bin", false),
		("an empty body", "", false),
		// Not ASCII whitespace, so every engine reads it the same way: not the note.
		("the note after a no-break space", "\u00a0" + Note, false),
	];

	// A set's members in ordinal order, so two sets compare as sequences.
	static string[] Sorted(IEnumerable<string> items) => [.. items.Order(StringComparer.Ordinal)];

	// COPILOT_NO_REVIEW from pr-automerge.yml, as .NET reads the same source.
	static Regex JsPattern()
	{
		var source = Skill.Workflow("pr-automerge.yml");
		var match = Regex.Match(source, "const COPILOT_NO_REVIEW = /(.+)/;");
		Assert.True(match.Success, "pr-automerge.yml defines COPILOT_NO_REVIEW as a regex literal");
		// Without the m flag, JS ^ is the start of the string, as .NET's \A is.
		var pattern = match.Groups[1].Value;
		var caret = pattern.IndexOf('^', StringComparison.Ordinal);
		return new Regex(caret < 0 ? pattern : pattern[..caret] + @"\A" + pattern[(caret + 1)..]);
	}

	// The reviewed-list jq filter from claude-review.yml, unquoted from the shell.
	static string JqProgram()
	{
		var source = Skill.Workflow("claude-review.yml");
		var match = Regex.Match(source, @"jq -r --arg head ""\$CURRENT_HEAD"" '(.+?)' ""\$\{RUNNER_TEMP\}/reviews\.json"" > ""\$\{RUNNER_TEMP\}/reviewed\.txt""",
				RegexOptions.Singleline);
		Assert.True(match.Success, "claude-review.yml builds reviewed.txt with a jq filter");
		return match.Groups[1].Value.Replace("'\"'\"'", "'", StringComparison.Ordinal);
	}

	// Every body's name, so a body added to Bodies is checked too.
	public static TheoryData<string> BodyNames => new(Bodies.Select(b => b.Name));

	[Theory]
	[MemberData(nameof(BodyNames))]
	public void Landing_and_pr_automerge_agree(string name)
	{
		var (_, body, isNote) = Bodies.Single(b => b.Name == name);
		Assert.Equal(isNote, Landing.CopilotNoReview.IsMatch(body));
		Assert.Equal(isNote, JsPattern().IsMatch(body));
	}

	static object Review(string login, string body, string commit) => new Dictionary<string, object> { ["user"] = new Dictionary<string, string> { ["login"] = login }, ["body"] = body, ["commit_id"] = commit };

	[Fact]
	public void Claude_review_jq_agrees()
	{
		var reviews = Bodies.Select(b => Review(Login, b.Body, b.Name)).ToList();
		Assert.Equal(Sorted(Bodies.Where(b => !b.IsNote).Select(b => b.Name)), Sorted(ReviewedByFilter(reviews)));
	}

	// The noted-list jq filter from claude-review.yml, unquoted from the shell.
	static string NotedProgram()
	{
		var source = Skill.Workflow("claude-review.yml");
		var match = Regex.Match(source, @"jq -r '([^']+)' ""\$\{RUNNER_TEMP\}/reviews\.json"" > ""\$\{RUNNER_TEMP\}/noted\.txt""");
		Assert.True(match.Success, "claude-review.yml builds noted.txt with a jq filter");
		return match.Groups[1].Value;
	}

	// jq's full path on PATH, or null, as shutil.which finds it.
	static string? Which(string tool) =>
			(Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
					.Select(dir => System.IO.Path.Combine(dir, tool)).FirstOrDefault(File.Exists);

	// Runs engine with args on input, checks it exited 0 (subprocess.run's check=True), and gives its output's non-empty lines.
	static HashSet<string> RunLines(string engine, string[] args, string input)
	{
		var start = new ProcessStartInfo(engine) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (var arg in args)
			start.ArgumentList.Add(arg);
		using var run = Process.Start(start)!;
		var err = run.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		var output = run.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		run.StandardInput.Write(input);
		run.StandardInput.Close();
		run.WaitForExit();
		Assert.True(run.ExitCode == 0, $"{engine} failed: {err.Result}");
		return [.. output.Result.Split('\n').Where(line => line != "")];
	}

	// The reviews split into two pages, as gh api --slurp reads them, as JSON.
	static string Pages(List<object> reviews) => JsonSerializer.Serialize(new[] { reviews[..(reviews.Count / 2)], reviews[(reviews.Count / 2)..] });

	[Fact]
	public void Claude_review_noted_filter_lists_only_commits_with_copilots_note()
	{
		var claude = "<!-- claude-review -->\nNo findings.";
		List<object> reviews =
		[
			Review("github-actions[bot]", claude, "claude only"),
			Review("someone", Note, "not copilot"),
			.. Bodies.Select(b => Review(Login, b.Body, b.Name)),
		];
		if (Which("jq") is not { } jq)
		{
			Assert.Fail("jq isn't installed, so claude-review.yml's filter can't be checked");
			return;
		}
		var lines = RunLines(jq, ["-r", NotedProgram()], Pages(reviews));
		// Every spelling of the note agrees on the shared bodies, so a drifted pattern here fails too.
		Assert.Equal(Sorted(Bodies.Where(b => b.IsNote).Select(b => b.Name)), Sorted(lines));
	}

	// The commits claude-review.yml's filter lists for a PR whose head is head, over reviews split into two pages as gh api --slurp reads
	// them.
	static HashSet<string> ReviewedByFilter(List<object> reviews, string head = "head")
	{
		var engine = Which("jq");
		if (engine is null)
			Assert.Fail("jq isn't installed, so claude-review.yml's filter can't be checked");
		return RunLines(engine, ["-r", "--arg", "head", head, JqProgram()], Pages(reviews));
	}

	[Fact]
	public void Claude_review_counts_in_the_jq_filter_for_every_commit_it_reviewed()
	{
		var claude = "<!-- claude-review -->\nNo findings.";
		List<object> reviews =
		[
			Review("github-actions[bot]", claude, "noted"),
			Review("github-actions[bot]", claude, "earlier"),
			Review("github-actions[bot]", claude, "head"),
			Review(Login, Note, "noted"),
			Review(Login, Note, "elsewhere"),
		];
		// Every Claude review is listed, whatever its commit (mpaulosky/dotfiles#375): a clean merge of main into any commit
		// a reviewer reviewed needs no paid review and is no round. Copilot's note itself covers nothing.
		Assert.Equal(Sorted(["noted", "earlier", "head"]), Sorted(ReviewedByFilter(reviews)));
	}
}
