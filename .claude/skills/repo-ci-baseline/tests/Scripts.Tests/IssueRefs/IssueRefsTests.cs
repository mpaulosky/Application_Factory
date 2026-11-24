// The tests of IssueRefs.cs (tests/check-issue-refs.cs), moved from the pytest of check-issue-refs.py (tests/issue_refs/), one test method each:
// what it flags, what it lets through, and that a bad folder is never a pass. tests/check-issue-refs.test.sh holds it to the Python through
// its command line. The modes it sets are Unix file modes.
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class IssueRefsTests
{
	sealed record Result(int Code, string Stdout, string Stderr);

	// The checker on these arguments, in this process; the timeout fails a checker that blocks (on a FIFO) instead of hanging the run.
	static Result Run(params string[] args)
	{
		var stdout = new StringWriter();
		var stderr = new StringWriter();
		var run = Task.Run(() => Checker.Run("check-issue-refs.cs", (o, e) => IssueRefs.Run(args, o, e), stdout, stderr));
		Assert.True(run.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken), "the checker didn't finish in 20 seconds");
		return new Result(run.Result, stdout.ToString(), stderr.ToString());
	}

	static string Tree(Dictionary<string, string> files, string? below = null)
	{
		var root = Directory.CreateTempSubdirectory("issue-refs-test.").FullName;
		if (below is not null)
			root = Directory.CreateDirectory(Path.Combine(root, below)).FullName;
		foreach (var (name, text) in files)
		{
			var path = Path.Combine(root, name);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, text);
		}
		return root;
	}

	// Whether the user reads past a mode of 0 (root does), so a test of an unreadable file can't run.
	static bool Readable(string path)
	{
		try
		{
			if (Directory.Exists(path))
				Directory.EnumerateFileSystemEntries(path).ToList();
			else
				File.ReadAllBytes(path);
			return true;
		}
		catch (Exception e) when (e is UnauthorizedAccessException or IOException)
		{
			return false;
		}
	}

	[Fact]
	public void A_bare_reference_in_prose_is_reported_with_its_line()
	{
		var result = Run(Tree(new() { ["docs/a.md"] = "fine\nSee #5 for why.\n" }));
		Assert.Equal(1, result.Code);
		Assert.Contains("docs/a.md:2: See #5 for why.", result.Stdout);
	}

	[Fact]
	public void A_repo_qualified_reference_passes() =>
			Assert.Equal(0, Run(Tree(new() { ["a.md"] = "See mpaulosky/dotfiles#5 and Application_Factory#6.\n" })).Code);

	[Fact]
	public void A_parenthesised_citation_is_reported_even_under_tests()
	{
		var result = Run(Tree(new() { ["scripts/tests/t.sh"] = "# a fix (#5)\n" }));
		Assert.True(result.Code == 1 && result.Stdout.Contains("scripts/tests/t.sh:1"));
	}

	[Fact]
	public void A_bare_reference_under_tests_outside_a_citation_passes() =>
			Assert.Equal(0, Run(Tree(new() { ["scripts/tests/t.sh"] = "echo '#5 is fixture data'\n" })).Code);

	[Fact]
	public void A_sandcastle_test_checks_only_comment_lines()
	{
		Assert.Equal(0, Run(Tree(new() { ["x.test.mts"] = "const s = \"see #5\";\n" })).Code);
		Assert.Equal(1, Run(Tree(new() { ["y.test.mts"] = "// see #5\n" })).Code);
	}

	[Fact]
	public void The_listed_examples_and_html_entities_pass() =>
			Assert.Equal(0, Run(Tree(new() { ["a.md"] = "Write `Fixes #123` and &#9; here.\n" })).Code);

	[Fact]
	public void An_example_does_not_hide_a_longer_number() =>
			Assert.Equal(1, Run(Tree(new() { ["a.md"] = "Fixes #421\n" })).Code);

	[Fact]
	public void A_missing_folder_or_no_argument_is_not_a_pass()
	{
		var root = Tree([]);
		Assert.Equal(2, Run(Path.Combine(root, "nope")).Code);
		Assert.Equal(2, Run().Code);
		Assert.Equal(2, Run(root, "extra").Code);
	}

	[Fact]
	public void An_unreadable_file_exits_2_and_hides_the_findings_found_before_it()
	{
		var root = Tree(new() { ["a.md"] = "See #5\n", ["b.md"] = "fine\n" });
		var b = Path.Combine(root, "b.md");
		File.SetUnixFileMode(b, UnixFileMode.None);
		try
		{
			Assert.SkipWhen(Readable(b), "root reads anything");
			var result = Run(root);
			Assert.True(result.Code == 2 && result.Stderr.Contains("b.md"), result.Stderr);
			Assert.Equal("", result.Stdout);
		}
		finally
		{
			File.SetUnixFileMode(b, (UnixFileMode)0b110_100_100);
		}
	}

	[Theory]
	[InlineData("sub")]
	[InlineData(".")]
	public void An_unlistable_directory_exits_2_instead_of_skipping_its_files(string unreadable)
	{
		var root = Tree(new() { ["sub/b.md"] = "See #5\n", ["a.md"] = "fine\n" }, "owned");
		var target = Path.GetFullPath(Path.Combine(root, unreadable));
		File.SetUnixFileMode(target, UnixFileMode.None);
		try
		{
			Assert.SkipWhen(Readable(target), "root lists anything");
			var result = Run(root);
			Assert.True(result.Code == 2 && result.Stdout == "", result.Stdout);
			Assert.Contains(target, result.Stderr);
		}
		finally
		{
			File.SetUnixFileMode(target, (UnixFileMode)0b111_101_101);
		}
	}

	[Fact]
	public void A_directory_that_lists_but_cannot_be_searched_exits_2()
	{
		// Mode 0o600: the names can be listed, but each file inside can't be statted or opened.
		var root = Tree(new() { ["sub/b.md"] = "See #5\n", ["a.md"] = "fine\n" }, "owned");
		var sub = Path.Combine(root, "sub");
		File.SetUnixFileMode(sub, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		try
		{
			Assert.SkipWhen(Readable(Path.Combine(sub, "b.md")), "root reads anything");
			var result = Run(root);
			Assert.True(result.Code == 2 && result.Stdout == "", result.Stdout);
			Assert.Contains("b.md", result.Stderr);
		}
		finally
		{
			File.SetUnixFileMode(sub, (UnixFileMode)0b111_101_101);
		}
	}

	[Fact]
	public void Names_that_resolve_to_nothing_are_skipped_without_hiding_a_real_finding()
	{
		var root = Tree(new() { ["a.md"] = "fine\n", ["real.md"] = "See #5\n" }, "owned");
		File.CreateSymbolicLink(Path.Combine(root, "dangling.md"), Path.Combine(root, "missing.md")); // ENOENT
		File.CreateSymbolicLink(Path.Combine(root, "loop.md"), "loop.md"); // ELOOP
		File.CreateSymbolicLink(Path.Combine(root, "through.md"), Path.Combine(root, "a.md", "x")); // ENOTDIR
		var result = Run(root);
		Assert.True(result.Code == 1 && result.Stderr == "", result.Stderr);
		Assert.Equal(["real.md:1: See #5"], result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
	}

	[Fact]
	public void A_fifo_is_skipped_not_opened()
	{
		var root = Tree(new() { ["a.md"] = "fine\n" }, "owned");
		var mkfifo = System.Diagnostics.Process.Start("mkfifo", [Path.Combine(root, "pipe.md")])!;
		mkfifo.WaitForExit();
		Assert.Equal(0, mkfifo.ExitCode);
		var result = Run(root); // opening a FIFO with no writer would block until the timeout
		Assert.True(result.Code == 0 && result.Stdout == "" && result.Stderr == "", result.Stderr);
	}
}
