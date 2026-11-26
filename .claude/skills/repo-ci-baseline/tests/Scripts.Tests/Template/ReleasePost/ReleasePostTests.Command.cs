namespace Baseline.Tests;

// ReleasePost.Command, release_post.py's main(), which the cut-over gave release_post.cs (mpaulosky/Application_Factory#128): the port's
// own tests, since the pytest had none of main(). The parity suite runs the entry file through the same cases.
public sealed partial class ReleasePostTests
{
	// main(argv) in tmp, with gh and the environment stood in for: its exit code, stdout and stderr, and the repository gh was made for.
	(int Code, string Out, string Err, string? Repository) Command(string[] argv, Dictionary<string, string>? environment = null)
	{
		StringWriter stdout = new(), stderr = new();
		string? repository = null;
		MakeRepo(tmp);
		var code = ReleasePost.Command(argv, stdout, stderr, name =>
		{
			repository = name;
			return new FakeGitHub();
		}, name => environment?.GetValueOrDefault(name), tmp);
		return (code, stdout.ToString(), stderr.ToString(), repository);
	}

	const string UsageLine = "usage: release_post.cs [-h] --pr PR --tag TAG [--repo REPO]\n";

	[Theory]
	[InlineData(new[] { "--pr", "42", "--tag", "v0.0.3" }, "REPOSITORY=o/one GITHUB_REPOSITORY=o/two", "o/one")]
	[InlineData(new[] { "--pr", "42", "--tag", "v0.0.3" }, "REPOSITORY= GITHUB_REPOSITORY=o/two", "o/two")]
	[InlineData(new[] { "--pr", "42", "--tag", "v0.0.3", "--repo", "o/three" }, "REPOSITORY=o/one", "o/three")]
	public void The_repository_is_repo_then_repository_then_github_repository(string[] argv, string variables, string expected)
	{
		var environment = variables.Split(' ').Select(pair => pair.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1]);

		var (code, _, _, repository) = Command(argv, environment);

		Assert.Equal(0, code);
		Assert.Equal(expected, repository);
	}

	[Theory]
	[InlineData(new[] { "--pr", "42", "--tag", "v0.0.3" }, "--repo is required when REPOSITORY and GITHUB_REPOSITORY are unset")]
	[InlineData(new[] { "--pr", "42", "--tag", "v0.0.3", "--repo", "" }, "--repo is required when REPOSITORY and GITHUB_REPOSITORY are unset")]
	[InlineData(new[] { "--pr", "42" }, "the following arguments are required: --tag")]
	[InlineData(new[] { "--pr", "seven", "--tag", "v0.0.3" }, "argument --pr: invalid int value: 'seven'")]
	public void A_usage_error_exits_2_before_any_gh_call(string[] argv, string message)
	{
		var (code, output, err, repository) = Command(argv);

		Assert.Equal(2, code);
		Assert.Equal("", output);
		Assert.Equal($"{UsageLine}release_post.cs: error: {message}\n", err);
		Assert.Null(repository);
	}

	[Fact]
	public void The_tag_is_stripped_and_the_pr_read_as_int_reads_it()
	{
		var (code, output, _, _) = Command(["--pr", " +0_42 ", "--ta", "  v0.0.3\t", "--repo", Repo]);

		Assert.Equal(0, code);
		Assert.Contains("Wrote docs/blogs/2026-09-24-pr-42-feat-ui-add-the-dark-theme.md\n", output);
		Assert.Contains("- **Release tag:** `v0.0.3`", Read(Path.Join(tmp, "docs", "blogs", "2026-09-24-pr-42-feat-ui-add-the-dark-theme.md")));
	}

	[Theory]
	[InlineData(null)]
	[InlineData(" \t ")]
	public void A_key_that_is_unset_or_only_blanks_means_no_summary(string? key)
	{
		Dictionary<string, string> environment = new() { ["ANTHROPIC_MODEL"] = "  " };
		if (key is not null)
			environment["ANTHROPIC_API_KEY"] = key;

		var (code, output, _, _) = Command(["--pr", "42", "--tag", "v0.0.3", "--repo", Repo], environment);

		Assert.Equal(0, code);
		Assert.StartsWith("::notice::ANTHROPIC_API_KEY is not set; writing the post without an AI summary.\n", output);
	}
}
