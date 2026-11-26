using System.Diagnostics;

namespace Baseline.Tests;

// The Template's .github/scripts/DetectChanges.cs: test_detect_changes.py's tests, one method each under its name (ADR 0017's rule), with
// the same cases and assertions, on throwaway git repos. In Baseline's namespace, so Script and Arguments are the Template's, not github/'s.
public class DetectChangesTests
{
	const string ActionsBranch = "dependabot/github_actions/all-actions-1a2b3c";

	static void Git(string repo, params string[] args)
	{
		var start = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (var arg in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.test" }.Concat(args))
			start.ArgumentList.Add(arg);
		using var run = Process.Start(start)!;
		var err = run.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		run.StandardOutput.ReadToEnd();
		run.WaitForExit();
		Assert.True(run.ExitCode == 0, $"git {string.Join(" ", args)}: {err.Result}");
	}

	static void Write(string repo, string path, string text)
	{
		var full = Path.Combine(repo, path);
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, text);
	}

	// A repo whose first commit holds some code and some docs; returns (path, base sha). The folder is removed when the test ends.
	sealed class Repo : IDisposable
	{
		public readonly string Path = Directory.CreateTempSubdirectory("detect-changes.").FullName;
		readonly string outputs = Directory.CreateTempSubdirectory("detect-changes-out.").FullName;
		public readonly string Base;

		public Repo()
		{
			Git(Path, "init", "-q", "-b", "main");
			Write(Path, "src/App.cs", "class App {}\n");
			Write(Path, "docs/guide.md", "# Guide\n");
			Write(Path, "README.md", "# Readme\n");
			Git(Path, "add", "-A");
			Git(Path, "commit", "-q", "-m", "base");
			Base = SurrogateEscape.Decode(Script.CheckOutput(["git", "rev-parse", "HEAD"], Path)).Trim();
		}

		public void Commit(string message = "change")
		{
			Git(Path, "add", "-A");
			Git(Path, "commit", "-q", "--allow-empty", "-m", message);
		}

		// An output file outside the repo, holding text.
		public string Output(string text = "")
		{
			var file = System.IO.Path.Combine(outputs, "github_output");
			File.WriteAllText(file, text);
			return file;
		}

		public void Dispose()
		{
			Directory.Delete(Path, true);
			Directory.Delete(outputs, true);
		}
	}

	static bool Classify(Repo repo) => DetectChanges.IsCodeChange(DetectChanges.ChangedPaths(repo.Base, cwd: repo.Path));

	// main(argv) with the repo as the current folder: (exit code, stdout).
	static (int Code, string Stdout) RunMain(Repo repo, params string[] args)
	{
		var stdout = new StringWriter();
		var code = DetectChanges.Run(args, stdout, new StringWriter(), repo.Path);
		return (code, stdout.ToString());
	}

	[Theory]
	[InlineData("docs/blogs/2026-09-30-pr-1-x.md", true)]
	[InlineData("docs/index.html", true)]
	[InlineData("README.md", true)]
	[InlineData("src/App/README.md", true)]
	[InlineData("src/App.cs", false)]
	[InlineData(".github/workflows/ci.yml", false)]
	[InlineData("scripts/gate.sh", false)]
	[InlineData("docsite/page.md", true)]
	[InlineData("docsite/page.html", false)]
	[InlineData("README.MD", false)]
	[InlineData("src/docs/notes.txt", false)]
	public void Is_doc(string path, bool doc) => Assert.Equal(doc, DetectChanges.IsDoc(path));

	[Fact]
	public void A_docs_only_change_skips_the_build()
	{
		using var repo = new Repo();
		Write(repo.Path, "docs/guide.md", "# Guide\n\nMore.\n");
		Write(repo.Path, "docs/index.html", "<p>new</p>\n");
		Write(repo.Path, "README.md", "# Readme\n\nMore.\n");
		repo.Commit();

		Assert.False(Classify(repo));
	}

	[Fact]
	public void A_code_change_runs_the_build()
	{
		using var repo = new Repo();
		Write(repo.Path, "src/App.cs", "class App { }\n");
		repo.Commit();

		Assert.True(Classify(repo));
	}

	[Fact]
	public void A_mixed_change_runs_the_build()
	{
		using var repo = new Repo();
		Write(repo.Path, "docs/guide.md", "# Guide\n\nMore.\n");
		Write(repo.Path, ".github/ci.yml", "on: push\n");
		repo.Commit();

		Assert.True(Classify(repo));
	}

	[Fact]
	public void An_empty_diff_runs_the_build()
	{
		using var repo = new Repo();
		repo.Commit("empty");

		Assert.Empty(DetectChanges.ChangedPaths(repo.Base, cwd: repo.Path));
		Assert.True(Classify(repo));
	}

	[Fact]
	public void Moving_code_into_docs_runs_the_build()
	{
		using var repo = new Repo();
		Git(repo.Path, "mv", "src/App.cs", "docs/App.cs");
		repo.Commit();

		Assert.Equal(["docs/App.cs", "src/App.cs"], DetectChanges.ChangedPaths(repo.Base, cwd: repo.Path));
		Assert.True(Classify(repo));
	}

	[Fact]
	public void Renaming_code_to_a_md_name_runs_the_build()
	{
		using var repo = new Repo();
		Git(repo.Path, "mv", "src/App.cs", "src/App.md");
		repo.Commit();

		Assert.True(Classify(repo));
	}

	[Fact]
	public void Deleting_code_runs_the_build()
	{
		using var repo = new Repo();
		Git(repo.Path, "rm", "-q", "src/App.cs");
		repo.Commit();

		Assert.True(Classify(repo));
	}

	[Fact]
	public void Paths_with_spaces_and_newlines_are_kept_whole()
	{
		using var repo = new Repo();
		Write(repo.Path, "docs/a file.md", "x\n");
		Write(repo.Path, "docs/odd\nname.md", "x\n");
		repo.Commit();

		Assert.Equal(["docs/a file.md", "docs/odd\nname.md"], DetectChanges.ChangedPaths(repo.Base, cwd: repo.Path).Order(StringComparer.Ordinal));
		Assert.False(Classify(repo));
	}

	[Fact]
	public void Main_writes_the_output_and_a_notice()
	{
		using var repo = new Repo();
		Write(repo.Path, "docs/guide.md", "# Guide\n\nMore.\n");
		repo.Commit();
		var output = repo.Output("earlier=1\n");

		var (code, stdout) = RunMain(repo, "--base", repo.Base, "--output", output);

		Assert.Equal(0, code);
		Assert.Equal("earlier=1\ncode=false\nskip_reason=docs-only\n", File.ReadAllText(output));
		Assert.Contains("::notice::Docs-only change (1 files)", stdout);
	}

	[Fact]
	public void Main_names_the_first_code_path()
	{
		using var repo = new Repo();
		Write(repo.Path, "src/App.cs", "class App { }\n");
		repo.Commit();
		var output = repo.Output();

		var (_, stdout) = RunMain(repo, "--base", repo.Base, "--output", output);

		Assert.Equal("code=true\nskip_reason=\n", File.ReadAllText(output));
		Assert.Contains("Code change: src/App.cs", stdout);
	}

	[Fact]
	public void A_code_path_with_line_breaks_cant_start_a_workflow_command()
	{
		using var repo = new Repo();
		Write(repo.Path, "src/x\n::error::forged\r::warning::also", "x\n");
		repo.Commit();
		var output = repo.Output();

		var (_, stdout) = RunMain(repo, "--base", repo.Base, "--output", output);

		Assert.Equal(["Code change: src/x\\n::error::forged\\r::warning::also"], stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
		Assert.Equal("code=true\nskip_reason=\n", File.ReadAllText(output));
	}

	[Fact]
	public void An_unknown_base_fails()
	{
		using var repo = new Repo();

		Assert.Throws<CalledProcessError>(() => RunMain(repo, "--base", new string('0', 40), "--output", repo.Output()));
	}

	public static TheoryData<string[], string, string, bool> Bumps => new()
	{
		{ [".github/workflows/ci.yml", ".github/actions/setup/action.yml"], "dependabot[bot]", ActionsBranch, true },
		// Every condition must hold.
		{ [".github/workflows/ci.yml"], "mpaulosky", ActionsBranch, false },
		{ [".github/workflows/ci.yml"], "dependabot[bot]", "dependabot/nuget/all-nuget-1a2b3c", false },
		{ [".github/workflows/ci.yml", "src/App.cs"], "dependabot[bot]", ActionsBranch, false },
		{ [".github/workflows/ci.yml", "global.json"], "dependabot[bot]", ActionsBranch, false },
		{ [], "dependabot[bot]", ActionsBranch, false },
	};

	[Theory]
	[MemberData(nameof(Bumps))]
	public void Is_actions_bump(string[] paths, string author, string headRef, bool bump) => Assert.Equal(bump, DetectChanges.IsActionsBump(paths, author, headRef));

	[Fact]
	public void A_dependabot_actions_bump_skips_the_build()
	{
		using var repo = new Repo();
		Write(repo.Path, ".github/workflows/ci.yml", "uses: actions/checkout@" + new string('b', 40) + "\n");
		repo.Commit();
		var output = repo.Output();

		var (_, stdout) = RunMain(repo, "--base", repo.Base, "--output", output, "--author", "dependabot[bot]", "--head-ref", ActionsBranch);

		Assert.Equal("code=false\nskip_reason=actions-bump\n", File.ReadAllText(output));
		Assert.Contains("::notice::Dependabot GitHub Actions bump (1 files)", stdout);
	}

	[Fact]
	public void The_same_change_by_anyone_else_runs_the_build()
	{
		using var repo = new Repo();
		Write(repo.Path, ".github/workflows/ci.yml", "uses: actions/checkout@" + new string('b', 40) + "\n");
		repo.Commit();
		var output = repo.Output();

		RunMain(repo, "--base", repo.Base, "--output", output, "--author", "mpaulosky", "--head-ref", ActionsBranch);

		Assert.Equal("code=true\nskip_reason=\n", File.ReadAllText(output));
	}
}
