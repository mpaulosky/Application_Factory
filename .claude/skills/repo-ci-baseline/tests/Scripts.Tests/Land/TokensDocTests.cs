using System.Text.RegularExpressions;

// Tests for references/tokens.md (mpaulosky/dotfiles#231): it lists every API call the Template's workflows make with RELEASE_PR_PAT or
// GITHUB_TOKEN, says whether each needs the PAT and why, and gives land.sh's poll interval and cost. No GitHub.
//
// The Template's workflows are the source of truth, so the page is checked against them: add a call to pr-automerge.yml and this fails
// until the page lists it.
public class TokensDocTests
{
	static string TOKENS => Skill.Path("references", "tokens.md");
	static string AUTOMERGE_DOC => Skill.Path("references", "automerge.md");

	// str.splitlines() for the docs' and workflows' line endings.
	static string[] SplitLines(string text)
	{
		var lines = Regex.Split(text, "\r\n|\n|\r");
		return text.EndsWith('\n') || text.EndsWith('\r') ? lines[..^1] : lines;
	}

	static int Indent(string line) => line.Length - line.TrimStart().Length;

	// The body of the workflow's `script: |` block, without its // comments.
	static string InlineScript(string workflow)
	{
		var lines = SplitLines(Skill.Workflow(workflow));
		var start = Array.FindIndex(lines, line => Regex.IsMatch(line, @"\A\s*script: \|\s*\z"));
		if (start < 0)
			throw new InvalidOperationException($"{workflow} has no `script: |` block");
		var indent = Indent(lines[start]);
		List<string> body = [];
		foreach (var line in lines[(start + 1)..])
		{
			if (line.Trim().Length > 0 && Indent(line) <= indent)
				break;
			body.Add(Regex.Replace(line, @"(^|\s)//.*$", "$1"));
		}
		return string.Join("\n", body);
	}

	// {(call name, "pat" | "token")} for every API call pr-automerge.yml's script makes.
	//
	// A call can be made through both clients: graphql (GITHUB_TOKEN reads the PR, the PAT requests Copilot's review) and
	// issues.createComment (GITHUB_TOKEN records that Copilot was asked, the PAT asks Dependabot to rebase), mpaulosky/dotfiles#360.
	// Each pair needs its own row.
	static HashSet<(string Call, string Client)> AutomergeCalls()
	{
		HashSet<(string, string)> calls = [];
		foreach (Match match in Regex.Matches(InlineScript("pr-automerge.yml"), @"\b(github|patClient)\.(graphql|rest\.\w+\.\w+)"))
			calls.Add((Regex.Replace(match.Groups[2].Value, @"^rest\.", ""), match.Groups[1].Value == "patClient" ? "pat" : "token"));
		return calls;
	}

	static string Page()
	{
		Assert.True(File.Exists(TOKENS), "references/tokens.md doesn't exist");
		return File.ReadAllText(TOKENS);
	}

	// The cells of each table row, as [workflow, call, client, why].
	static List<string[]> TableRows(string text)
	{
		List<string[]> rows = [];
		foreach (var line in SplitLines(text))
			if (line.StartsWith('|') && !Regex.IsMatch(line, @"\A[|\s:-]+\z"))
				rows.Add([.. line.Trim().Trim('|').Split('|').Select(cell => cell.Trim())]);
		return rows;
	}

	// The text from the first heading matching the pattern to the next heading of its level or higher.
	static string Section(string text, string headingPattern)
	{
		var lines = SplitLines(text);
		var start = Array.FindIndex(lines, line => Regex.IsMatch(line, @"\A#{2,4} ") && Regex.IsMatch(line, headingPattern));
		Assert.True(start >= 0, $"tokens.md has no heading matching '{headingPattern}'");
		var level = lines[start].Length - lines[start].TrimStart('#').Length;
		var end = Enumerable.Range(start + 1, lines.Length - start - 1).Where(i => Regex.IsMatch(lines[i], @"\A#{1," + level + "} ")).DefaultIfEmpty(lines.Length).First();
		return string.Join("\n", lines[start..end]);
	}

	[Fact]
	public void Pr_automerge_makes_calls_through_two_clients()
	{
		var calls = AutomergeCalls();

		Assert.True(calls.Count > 0, "found no API calls in pr-automerge.yml's script");
		Assert.True(calls.Select(c => c.Client).ToHashSet().SetEquals(["pat", "token"]), string.Join(", ", calls));
	}

	[Fact]
	public void Lists_every_pr_automerge_call_with_its_client_and_why()
	{
		var rows = TableRows(Page());

		foreach (var (call, client) in AutomergeCalls().OrderBy(c => c.Call, StringComparer.Ordinal).ThenBy(c => c.Client, StringComparer.Ordinal))
		{
			var listed = rows.Where(row => row.Length >= 4 && row[1].Contains($"`{call}", StringComparison.Ordinal)).ToList();
			Assert.True(listed.Count > 0, $"tokens.md has no table row for `{call}`");
			var matching = client == "pat"
				? listed.Where(row => row[2].Contains("PAT", StringComparison.Ordinal) && !row[2].Contains("GITHUB_TOKEN", StringComparison.Ordinal)).ToList()
				: listed.Where(row => row[2].Contains("GITHUB_TOKEN", StringComparison.Ordinal)).ToList();
			Assert.True(matching.Count > 0, $"tokens.md has no row for `{call}` through {client}: [{string.Join(", ", listed.Select(row => row[2]))}]");
			Assert.True(matching.All(row => row[3].Length > 0), $"`{call}`'s row gives no reason");
		}
	}

	[Fact]
	public void Pr_automerge_asks_copilot_with_the_pat_and_records_the_ask_with_github_token()
	{
		// mpaulosky/dotfiles#360: the one Copilot request is a person's call; the record comment is GITHUB_TOKEN's.
		var calls = AutomergeCalls();

		Assert.True(calls.Contains(("graphql", "pat")) && calls.Contains(("graphql", "token")));
		Assert.True(calls.Contains(("issues.createComment", "token")) && calls.Contains(("issues.createComment", "pat")));
		var rows = TableRows(Page());
		var request = rows.Where(row => row.Length >= 4 && row[2].Contains("PAT", StringComparison.Ordinal) && row[1].Contains("graphql", StringComparison.Ordinal)
				&& Regex.IsMatch(row[1], "requestReviews|Copilot")).ToList();
		var record = rows.Where(row => row.Length >= 4 && row[2].Contains("GITHUB_TOKEN", StringComparison.Ordinal) && row[1].Contains("createComment", StringComparison.Ordinal)
				&& Regex.IsMatch(row[1], "Copilot|asked|record")).ToList();
		Assert.True(request.Count > 0, "tokens.md has no PAT row for the graphql call that requests Copilot's review (name Copilot or requestReviewsByLogin)");
		Assert.True(record.Count > 0, "tokens.md has no GITHUB_TOKEN row for the comment that records Copilot was asked");
	}

	[Fact]
	public void Documents_the_exhaustion_comment_and_the_deletion_of_a_duplicate_with_github_token()
	{
		// mpaulosky/dotfiles#375: PR Auto-Merge posts one comment when a PR's review loop is exhausted, and a run that
		// finds another run's comment ahead of its own deletes its own. Both are GITHUB_TOKEN's (pull-requests: write).
		var calls = AutomergeCalls();

		Assert.Contains(("issues.deleteComment", "token"), calls);
		Assert.DoesNotContain(("issues.deleteComment", "pat"), calls);
		var rows = TableRows(Page());
		var posted = rows.Where(row => row.Length >= 4 && row[2].Contains("GITHUB_TOKEN", StringComparison.Ordinal) && row[1].Contains("createComment", StringComparison.Ordinal)
				&& Regex.IsMatch(string.Join(" ", row), "exhaust", RegexOptions.IgnoreCase)).ToList();
		var deleted = rows.Where(row => row.Length >= 4 && row[2].Contains("GITHUB_TOKEN", StringComparison.Ordinal) && row[1].Contains("deleteComment", StringComparison.Ordinal)).ToList();
		Assert.True(posted.Count > 0, "tokens.md has no GITHUB_TOKEN row for the comment that documents an exhausted review loop");
		Assert.True(deleted.Count > 0 && deleted[0][3].Length > 0, "tokens.md has no GITHUB_TOKEN row, with a reason, for deleting a duplicate exhaustion comment");
	}

	[Fact]
	public void Documents_the_collaborator_permission_read_that_decides_who_restarts_a_loop_with_github_token()
	{
		// mpaulosky/dotfiles#378: only a login with write access restarts a review loop, so PR Auto-Merge reads the permission
		// of each login that added review:claude after the loop's exhausting round (GITHUB_TOKEN, a read).
		var calls = AutomergeCalls();

		Assert.Contains(("repos.getCollaboratorPermissionLevel", "token"), calls);
		Assert.DoesNotContain(("repos.getCollaboratorPermissionLevel", "pat"), calls);
		var rows = TableRows(Page());
		var listed = rows.Where(row => row.Length >= 4 && row[1].Contains("getCollaboratorPermissionLevel", StringComparison.Ordinal)
				&& row[2].Contains("GITHUB_TOKEN", StringComparison.Ordinal)).ToList();
		Assert.True(listed.Count > 0 && listed[0][3].Length > 0, "tokens.md has no GITHUB_TOKEN row, with a reason, for repos.getCollaboratorPermissionLevel");
	}

	[Fact]
	public void Tokens_md_no_longer_says_the_label_comes_off_at_the_review_cap()
	{
		// The label comes off when the review loop is exhausted (mpaulosky/dotfiles#375), not at a cap of reviewed commits.
		var text = Page();

		Assert.False(Regex.IsMatch(text, "review cap|MAX_CLAUDE_ROUNDS commits|reviewed MAX_CLAUDE_ROUNDS"),
				"tokens.md still describes taking review:claude off at the review cap or after MAX_CLAUDE_ROUNDS reviewed commits");
		Assert.Matches(new Regex("exhaust", RegexOptions.IgnoreCase), text);
	}

	[Theory]
	[InlineData("gh pr list")]
	[InlineData("gh api")]
	[InlineData("gh pr edit")]
	[InlineData("gh pr create")]
	[InlineData("gh pr merge")]
	[InlineData("git push")]
	[InlineData("create-pull-request")]
	public void Lists_the_release_and_backfill_workflows_calls(string call)
	{
		var rows = TableRows(Page());

		var listed = rows.Where(row => row.Length >= 4 && Regex.IsMatch(row[1], $"`[^`]*{Regex.Escape(call)}[^`]*`")).ToList();
		Assert.True(listed.Count > 0, $"tokens.md has no table row for `{call}`");
		Assert.True(listed[0][3].Length > 0, $"`{call}`'s row gives no reason");
		Assert.Matches("PAT|GITHUB_TOKEN", listed[0][2]);
	}

	[Fact]
	public void Moves_the_blog_pr_lookups_and_label_to_github_token()
	{
		var rows = TableRows(Page());

		foreach (var call in new[] { "gh pr list", "gh api" })
		{
			var listed = rows.Where(row => row.Length >= 4 && row[1].Contains($"`{call}", StringComparison.Ordinal)).ToList();
			Assert.True(listed.Count > 0 && listed.All(row => row[2].Contains("GITHUB_TOKEN", StringComparison.Ordinal)), call);
		}
		foreach (var call in new[] { "gh pr edit", "gh pr create", "gh pr merge" })
		{
			var listed = rows.Where(row => row.Length >= 4 && row[1].Contains($"`{call}", StringComparison.Ordinal)).ToList();
			Assert.True(listed.Count > 0 && listed.All(row => row[2].Contains("PAT", StringComparison.Ordinal) && !row[2].Contains("GITHUB_TOKEN", StringComparison.Ordinal)), call);
		}
	}

	[Fact]
	public void Says_where_sandcastle_s_token_stands()
	{
		var text = Page();

		Assert.Contains("SANDCASTLE_GH_TOKEN", text, StringComparison.Ordinal);
		Assert.Contains("RELEASE_PR_PAT", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Explains_the_shared_allowance_and_the_incident()
	{
		var text = Page();

		Assert.Contains("5,000", text, StringComparison.Ordinal);
		Assert.Matches(@"shar(e|es|ed)\b", text);
		Assert.Contains("rate limit", text.ToLowerInvariant(), StringComparison.Ordinal);
		Assert.Contains("2026-10-18", text, StringComparison.Ordinal);
	}

	// land.sh's --interval default: the Python read it from the parser land.main builds; Land.Run's parser reads it from here.
	static double DefaultInterval() => Land.DefaultInterval;

	[Fact]
	public void Gives_land_sh_s_default_poll_interval()
	{
		var interval = DefaultInterval();
		var text = Section(Page(), @"land\.sh");

		Assert.True(Regex.IsMatch(text, $@"\b{Script.G(interval)} ?s(econds)?\b"), $"the land.sh section doesn't give the {Script.G(interval)} s default");
		Assert.Contains("--interval", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Gives_the_requests_a_land_sh_round_makes_and_the_shared_allowance()
	{
		var text = Section(Page(), @"land\.sh");

		Assert.Matches("GraphQL", text);
		Assert.Matches(@"\bper (repo|round|poll)\b|\ba round\b", text);
		Assert.Contains("5,000", text, StringComparison.Ordinal);
		Assert.Matches(@"shar(e|es|ed)\b", text);
		Assert.Contains("gh api rate_limit", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Automerge_md_gives_the_same_default_interval_and_points_at_tokens_md()
	{
		var interval = DefaultInterval();
		var text = File.ReadAllText(AUTOMERGE_DOC);
		var landing = text[text.IndexOf("### Landing a round", StringComparison.Ordinal)..];

		Assert.Contains($"default every {Script.G(interval)} s", landing, StringComparison.Ordinal);
		Assert.Contains("tokens.md", landing, StringComparison.Ordinal);
		Assert.True(text.Split("### Landing a round")[0].Contains("tokens.md", StringComparison.Ordinal), "the Token bullet doesn't link tokens.md");
	}

	[Fact]
	public void Skill_md_links_tokens_md()
	{
		Assert.Contains("references/tokens.md", File.ReadAllText(Skill.Path("SKILL.md")), StringComparison.Ordinal);
	}
}
