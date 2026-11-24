using System.Text.Json;
using System.Text.Json.Nodes;

// What the port changes on purpose (ADR 0017's Update for mpaulosky/Application_Factory#119): not moved tests, so StatusTests.cs keeps
// test_status.py's and its count can be checked against that file's.
public partial class StatusTests
{
	// Not a moved test: a fork's PR names its own branch, so a release-blog branch counts only on a PR from the repo itself.
	[Fact]
	public void A_forks_pr_on_a_release_blog_branch_is_no_release_blog_pr()
	{
		var blog = With(PrNode(), ("headRefName", "docs/backfill-blog-posts"));
		Assert.True(ToState(blog, Status.RepoFlags("full")).ReleaseBlog);
		Assert.False(ToState(With(blog, ("isCrossRepository", true)), Status.RepoFlags("full")).ReleaseBlog);
		Assert.False(ToState(With(blog, ("isCrossRepository", null)), Status.RepoFlags("full")).ReleaseBlog);
		Assert.False(ToState(With(blog, ("isCrossRepository", "false")), Status.RepoFlags("full")).ReleaseBlog);
		var missing = With(blog);
		missing.Remove("isCrossRepository");
		Assert.False(ToState(missing, Status.RepoFlags("full")).ReleaseBlog);
		Assert.Contains("isCrossRepository", Status.Query, StringComparison.Ordinal);
	}

	// Not a moved test: the title is PR-controlled, so what the terminal would act on is escaped, and the title is cut after that.
	[Fact]
	public void A_titles_control_and_format_characters_are_escaped_before_it_is_cut()
	{
		var (code, output, _) = Run(["owner/app"], new() { ["owner/app"] = Repo(PrNode(title: "a\u001b[31mb\u202ec\td\ne")) });
		Assert.Equal(0, code);
		In(@"a\x1b[31mb\u202ec\td\ne", output);
		NotIn("\u001b", output);
		NotIn("\u202e", output);
		Assert.Single(output.TrimEnd('\n').Split('\n'));
		// A title of control characters past the width is cut by what it prints as.
		var (_, cut, _) = Run(["owner/app"], new() { ["owner/app"] = Repo(PrNode(title: new string('\u0007', 20))) });
		var printed = string.Concat(Enumerable.Repeat(@"\x07", 20));
		Assert.True(printed.Length > Status.TitleWidth);
		In(Status.Shorten(printed), cut);
	}

	// Not a moved test: a repo that isn't owner/repo is a usage error before any gh call, since gh -F reads a value starting with @
	// from a file.
	[Theory]
	[InlineData("@/etc/passwd/x")]
	[InlineData("owner/app\n")]
	[InlineData("owner/app/x")]
	[InlineData("owner")]
	[InlineData("/app")]
	[InlineData("owner/")]
	[InlineData("own er/app")]
	public void A_repo_that_isnt_owner_repo_is_a_usage_error_before_any_call(string repo)
	{
		var fetched = 0;
		using var output = new StringWriter();
		using var err = new StringWriter();
		var code = Status.Run(["owner/app", repo], output, err, (_, _) => { fetched++; return Repo(); }, FleetList);
		Assert.Equal(2, code);
		Assert.Equal(0, fetched);
		In($"status.cs: error: not owner/repo: {repo}", err.ToString());
		Assert.Throws<ArgumentException>(() => Status.SplitRepo(repo));
		Assert.Equal(("o.x", "a_b-c"), Status.SplitRepo("o.x/a_b-c"));
	}

	// Not a moved test: an answer gh gave that isn't an object (the Python's .get raised on it) leaves the permission unread, and
	// isn't kept.
	[Theory]
	[InlineData("null")]
	[InlineData("[]")]
	[InlineData("\"write\"")]
	public void A_permission_answer_that_isnt_an_object_is_unread_and_not_kept(string answer)
	{
		var gh = new FakeGh(stdout: answer, code: 0);
		var reader = LookupWith(gh);

		Assert.True(reader.WriterAccess("owner", "app", User) is null);
		Assert.True(reader.WriterAccess("owner", "app", User) is null && gh.Calls == 2);
		// An object without the key is still a definite no.
		Assert.True(LookupWith(new FakeGh(stdout: "{}", code: 0)).WriterAccess("owner", "app", User) is false);
	}
}
